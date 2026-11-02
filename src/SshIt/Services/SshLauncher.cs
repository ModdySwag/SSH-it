using System.IO;
using System.Text;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Builds the command line for the OpenSSH client that ships with Windows.
/// Deliberately no re-implementation: every key type, ProxyJump, Match block,
/// certificate and FIDO token behaves exactly as it does in a terminal.
/// </summary>
public static class SshLauncher
{
    public static string SshExe
    {
        get
        {
            var system = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            return File.Exists(system) ? system : "ssh.exe";
        }
    }

    public static bool SshAvailable => Path.IsPathRooted(SshExe) ? File.Exists(SshExe) : true;

    public static string BuildArguments(Session session)
    {
        var args = new List<string> { "-tt" };

        if (session.UseConfigAlias && !string.IsNullOrWhiteSpace(session.ConfigAlias))
        {
            // Host alias owns host/port/user; explicit overrides still apply when set.
            AddCommon(args, session);
            args.Add(session.ConfigAlias!);
            return string.Join(' ', args.Select(Quote));
        }

        if (session.Port != 22)
        {
            args.Add("-p");
            args.Add(session.Port.ToString());
        }

        AddCommon(args, session);

        var target = string.IsNullOrWhiteSpace(session.User)
            ? session.Host
            : $"{session.User}@{session.Host}";
        args.Add(target);
        return string.Join(' ', args.Select(Quote));
    }

    /// <summary>
    /// The argv ssh hands to the remote shell for control mode. `-C` is control mode over a pipe:
    /// `-CC` is for a nested tmux client and calls tcgetattr on stdin, which fails on a raw pipe
    /// ("tcgetattr failed: Inappropriate ioctl for device"). The session name is quoted for the
    /// REMOTE shell, which is a different parser from the Windows command line one.
    /// </summary>
    public static string BuildTmuxCommand(string? sessionName)
    {
        var command = "tmux -C attach";
        if (!string.IsNullOrWhiteSpace(sessionName)) command += " -t " + RemoteQuote(sessionName.Trim());
        return command;
    }

    /// <summary>
    /// `ssh -tt <session> tmux -C attach` — the production transport. The remote command is one
    /// Windows argument, so the local parser cannot split it; ssh joins it back into one string for
    /// the remote shell.
    /// </summary>
    public static string BuildTmuxArguments(Session session, string? tmuxSession)
        => BuildCommandArguments(session, BuildTmuxCommand(tmuxSession));

    /// <summary>Any ssh invocation with a remote command appended to the session's settings.</summary>
    public static string BuildCommandArguments(Session session, string remoteCommand)
        => BuildArguments(session) + " " + QuoteArgument(remoteCommand);

    private static string RemoteQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Arguments for a one-shot diagnostic probe that never takes a terminal: "-G" prints the
    /// effective configuration, otherwise a verbose handshake for the same session settings.
    /// BatchMode keeps the probe from sitting at a password prompt it cannot answer.
    /// </summary>
    public static string BuildProbeArguments(Session session, bool effectiveConfig, string? command)
    {
        var args = new List<string>();
        if (effectiveConfig)
        {
            args.Add("-G");
        }
        else
        {
            args.Add("-vvv");
            args.Add("-T");
            args.Add("-o");
            args.Add("BatchMode=yes");
        }

        if (session.UseConfigAlias && !string.IsNullOrWhiteSpace(session.ConfigAlias))
        {
            AddCommon(args, session);
            args.Add(session.ConfigAlias!);
        }
        else
        {
            if (session.Port != 22)
            {
                args.Add("-p");
                args.Add(session.Port.ToString());
            }
            AddCommon(args, session);
            args.Add(string.IsNullOrWhiteSpace(session.User) ? session.Host : $"{session.User}@{session.Host}");
        }

        // The join below quotes every element once; quote a command here as well and a
        // command containing whitespace reaches the remote shell wrapped in literal quotes.
        if (!string.IsNullOrEmpty(command)) args.Add(command);
        return string.Join(' ', args.Select(QuoteArgument));
    }

    private static void AddCommon(List<string> args, Session session)
    {
        args.Add("-o");
        args.Add("ServerAliveInterval=30");
        args.Add("-o");
        args.Add("ServerAliveCountMax=3");
        args.Add("-o");
        args.Add("ConnectTimeout=20");
        args.Add("-o");
        args.Add("StrictHostKeyChecking=" + (session.StrictHostKeyChecking ? "yes" : "accept-new"));

        if (!string.IsNullOrWhiteSpace(session.KeyPath))
        {
            args.Add("-i");
            args.Add(session.KeyPath!);
            args.Add("-o");
            args.Add("IdentitiesOnly=yes");
        }

        if (!string.IsNullOrWhiteSpace(session.JumpHost))
        {
            // -J takes a comma-separated list with no spaces around the commas. A chain typed
            // as "a, b" otherwise fails at once with "invalid -J argument".
            var hops = JumpChain.Hops(session.JumpHost);

            if (hops.Count == 0)
            {
                // A blank chain is not a chain; adding -J with nothing after it refuses at once.
            }
            else if (string.IsNullOrWhiteSpace(session.KeyPath) && !session.HasPerHopKeys)
            {
                args.Add("-J");
                args.Add(string.Join(",", hops));
            }
            else
            {
                // A key file given with -i is NOT passed to the jump connection: OpenSSH builds the
                // hop's own command line with -l/-p/-W and nothing else, so the hop never offers the
                // session's key and fails "Permission denied (publickey)" while the target would have
                // accepted it. Spell the same chain as ProxyCommands so every hop gets the key.
                args.Add("-o");
                args.Add("ProxyCommand=" + JumpCommandLine(session, hops, hops.Count - 1));
            }
        }

        if (!string.IsNullOrWhiteSpace(session.ProxyCommand))
        {
            args.Add("-o");
            args.Add("ProxyCommand=" + session.ProxyCommand);
        }

        if (session.ForwardAgent) args.Add("-A");
        if (session.Compression) args.Add("-C");
        if (session.X11Forwarding) args.Add("-X");
    }

    /// <summary>Environment additions for the child: forces a true-colour TERM.</summary>
    public static Dictionary<string, string> BuildEnvironment(Session session, string? askPassSecretId)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor"
        };

        // A GUI app started from a POSIX shell (Git Bash, MSYS) inherits HOME=/c/Users/…
        // which Windows OpenSSH cannot resolve, so it cannot find known_hosts or ~/.ssh/config.
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home) || home.StartsWith('/') || home.Contains('/') && !home.Contains(':'))
        {
            env["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // A Cygwin/MSYS agent socket is meaningless to the native ssh.exe. A pipe path in slash form
        // ("//./pipe/name") is NOT one of those: Windows ssh resolves it to the named pipe, and it is
        // the form an MSYS shell exports, so clearing it would remove a working agent.
        var agent = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrEmpty(agent)
            && agent.StartsWith('/')
            && !agent.StartsWith("//./pipe/", StringComparison.OrdinalIgnoreCase))
        {
            env["SSH_AUTH_SOCK"] = "";
        }

        // ssh hands the forwarded X connection to whatever DISPLAY names. Windows OpenSSH does
        // not pick one for us, so without this -X connects and the first GUI app fails on the
        // host with "cannot open display".
        if (session.X11Forwarding)
        {
            var display = X11Support.ResolveDisplay();
            if (display is not null) env["DISPLAY"] = display;
        }

        if (askPassSecretId is not null)
        {
            // ssh runs %SSH_ASKPASS% when it needs a password or key passphrase.
            // SshIt.exe --askpass resolves the secret from Credential Manager itself,
            // so the secret never travels in the environment block.
            env["SSH_ASKPASS"] = Environment.ProcessPath ?? "";
            env["SSH_ASKPASS_REQUIRE"] = "force";
            env["CONDUIT_ASKPASS"] = askPassSecretId;
        }

        return env;
    }

    /// <summary>QUICK CONNECT parser: user@host:port, ssh:// URLs and bare hosts.</summary>
    public static Session? ParseQuickConnect(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(6);

        string user = "", host = text;
        int port = 22;

        var at = text.LastIndexOf('@');
        if (at > 0)
        {
            user = text[..at];
            host = text[(at + 1)..];
        }

        // Bracketed IPv6: [::1]:2222
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close > 0)
            {
                var tail = host[(close + 1)..];
                host = host[1..close];
                if (tail.StartsWith(':') && int.TryParse(tail[1..], out var p)) port = p;
            }
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p))
            {
                port = p;
                host = host[..colon];
            }
        }

        if (string.IsNullOrWhiteSpace(host)) return null;

        return new Session
        {
            Host = host,
            User = user,
            Port = port,
            Name = string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}"
        };
    }

    /// <summary>Same connection options, but without forcing a TTY (for tunnels and one-shot commands).</summary>
    public static string BuildBatchArguments(Session session)
    {
        var args = new List<string>();
        if (session.UseConfigAlias && !string.IsNullOrWhiteSpace(session.ConfigAlias))
        {
            AddCommon(args, session);
            args.Add(session.ConfigAlias!);
            return string.Join(' ', args.Select(Quote));
        }

        if (session.Port != 22) { args.Add("-p"); args.Add(session.Port.ToString()); }
        AddCommon(args, session);
        args.Add(string.IsNullOrWhiteSpace(session.User) ? session.Host : $"{session.User}@{session.Host}");
        return string.Join(' ', args.Select(Quote));
    }

    /// <summary>Command line that opens an SSH subsystem channel (sftp) on stdio.</summary>
    public static string BuildSubsystemArguments(Session session, string subsystem)
    {
        var batch = BuildBatchArguments(session);
        // -s and the destination must both be present; ssh reads the subsystem name last.
        return "-s " + batch + " " + subsystem;
    }

    /// <summary>
    /// One stage of a ProxyJump chain, spelled so the hop is offered the session's key. Stage 0 is a
    /// direct connection to the first hop; a later stage reaches its hop through the previous stage's
    /// stdio, exactly as `-J` does internally, but with the identity options included.
    ///
    /// Only the OUTERMOST stage may use `%h:%p`: ssh expands percent tokens across the whole
    /// ProxyCommand string, nested ones included, so a literal `%h:%p` in an inner stage would be
    /// rewritten to the session's target and the chain would quietly skip the hops in between. The
    /// next hop is known here, so inner stages name it literally.
    /// </summary>
    private static string JumpCommandLine(Session session, IReadOnlyList<string> hops, int index)
    {
        var hop = JumpChain.ParseHop(hops[index]);
        var parts = new List<string> { QuoteArgument(SshExe) };

        // Each hop may carry its own key; a hop with none falls back to the session's, and a hop with
        // neither is left to the agent and the default identities (so no -i and no IdentitiesOnly).
        var key = session.KeyForHop(hops[index]);
        if (!string.IsNullOrWhiteSpace(key))
        {
            parts.Add("-i");
            parts.Add(QuoteArgument(key));
            parts.Add("-o");
            parts.Add("IdentitiesOnly=yes");
        }

        parts.Add("-o");
        parts.Add(QuoteArgument("StrictHostKeyChecking=" + (session.StrictHostKeyChecking ? "yes" : "accept-new")));
        parts.Add("-o");
        parts.Add("ConnectTimeout=20");

        parts.Add("-W");
        if (index == hops.Count - 1)
        {
            parts.Add("%h:%p");
        }
        else
        {
            var next = JumpChain.ParseHop(hops[index + 1]);
            var port = next.Port > 0 ? next.Port : 22;
            var host = next.Host.Contains(':') ? "[" + next.Host + "]" : next.Host;
            parts.Add(QuoteArgument(host + ":" + port));
        }

        if (index > 0)
        {
            parts.Add("-o");
            parts.Add(QuoteArgument("ProxyCommand=" + JumpCommandLine(session, hops, index - 1)));
        }

        if (hop.Port > 0) { parts.Add("-p"); parts.Add(hop.Port.ToString()); }
        if (hop.User.Length > 0) { parts.Add("-l"); parts.Add(QuoteArgument(hop.User)); }
        // A hop is written "user@host:port"; a bare destination is the only form the ssh command
        // line itself understands, so the pieces are split out rather than pasted back in.
        parts.Add(QuoteArgument(hop.Host));
        return string.Join(' ', parts);
    }

    private static string Quote(string value) => QuoteArgument(value);

    /// <summary>
    /// Quotes one argument for a Windows command line, following the CreateProcess rules:
    /// backslashes are only special before a double quote. A run of N backslashes stays N
    /// unless a quote follows it, where it becomes 2N+1. Doubling every backslash — the
    /// obvious version — turns the "\n" inside an awk printf into a literal "\\n", so the
    /// remote prints backslash-n instead of a newline and every line after it stops parsing.
    /// </summary>
    public static string QuoteArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                builder.Append(c);
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append(c);
            backslashes = 0;
        }

        builder.Append('\\', backslashes);
        builder.Append('"');
        return builder.ToString();
    }
}