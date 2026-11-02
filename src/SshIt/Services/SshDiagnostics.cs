using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Answers "why did that connection do what it did" from the two things OpenSSH already tells us:
/// <c>ssh -G</c> (the effective configuration after every Include, Match and wildcard) and a
/// <c>ssh -vvv</c> probe (the handshake, stage by stage). No network of our own, no guessing:
/// every line in the report comes from the user's own ssh binary.
/// </summary>
public static class SshDiagnostics
{
    public sealed class Step
    {
        public string Stage { get; init; } = "";
        public string Detail { get; init; } = "";
        public string State { get; init; } = "info";   // ok | fail | info
    }

    public sealed class Report
    {
        public string Host { get; init; } = "";
        public string CommandLine { get; init; } = "";
        public List<KeyValuePair<string, string>> Effective { get; init; } = new();
        public string EffectiveRaw { get; init; } = "";
        /// <summary>The raw stderr of the handshake probe. Kept so other readers (the fleet posture
        /// scan) can use the KEXINIT proposals, which the stage list deliberately ignores.</summary>
        public string ProbeLog { get; init; } = "";
        public List<Step> Steps { get; init; } = new();
        public string? FailureStage { get; init; }
        public string? FailureText { get; init; }
        public List<string> Hints { get; init; } = new();
        public bool Authenticated { get; init; }
        public int ExitCode { get; init; }
        public long DurationMs { get; init; }
        public string? ProbeError { get; init; }
    }

    // ---------------------------------------------------------------- running the probe

    /// <summary>
    /// Runs both probes. <c>-G</c> is instant and offline; the <c>-vvv</c> attempt is bounded by the
    /// session's ConnectTimeout and BatchMode, so it can never sit at a password prompt.
    /// </summary>
    public static async Task<Report> RunAsync(Session session, CancellationToken token)
    {
        var started = Environment.TickCount64;
        var effectiveArgs = SshLauncher.BuildProbeArguments(session, effectiveConfig: true, command: null);
        var probeArgs = SshLauncher.BuildProbeArguments(session, effectiveConfig: false, command: "true");

        var effective = await RunAsync(effectiveArgs, token);
        var probe = await RunAsync(probeArgs, token);

        var steps = ParseVerbose(probe.StdErr);
        var text = probe.StdErr + "\n" + probe.StdOut;
        var failure = steps.FirstOrDefault(s => s.State == "fail");
        var hints = HintsFor(text);

        // A non-zero exit with no debug line (ssh missing, bad quoting) still needs an answer.
        if (failure is null && probe.ExitCode != 0 && string.IsNullOrWhiteSpace(probe.StdErr))
            hints.Insert(0, $"ssh exited {probe.ExitCode} without writing a handshake log. Check that ssh.exe is on PATH.");

        return new Report
        {
            Host = session.DisplayTarget,
            CommandLine = probeArgs.Replace(SshLauncher.SshExe, "ssh"),
            Effective = ParseEffective(effective.StdOut),
            EffectiveRaw = effective.StdOut,
            ProbeLog = probe.StdErr,
            Steps = steps,
            FailureStage = failure?.Stage,
            FailureText = failure?.Detail,
            Hints = hints,
            Authenticated = steps.Any(s => s.Stage == "Authenticated" && s.State == "ok"),
            ExitCode = probe.ExitCode,
            DurationMs = Environment.TickCount64 - started,
            ProbeError = probe.StartError ?? effective.StartError
        };
    }

    private sealed class ProbeResult
    {
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
        public int ExitCode { get; init; }
        public string? StartError { get; init; }
    }

    private static async Task<ProbeResult> RunAsync(string arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(SshLauncher.SshExe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["TERM"] = "dumb";

        try
        {
            using var process = Process.Start(info);
            if (process is null) return new ProbeResult { StartError = "ssh did not start" };

            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return new ProbeResult
            {
                StdOut = await stdout,
                StdErr = await stderr,
                ExitCode = process.ExitCode
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeResult { StartError = ex.Message };
        }
    }

    // ---------------------------------------------------------------- ssh -G

    /// <summary>
    /// <c>ssh -G</c> prints one "key value" line per resolved option; identityfile (and a few
    /// others) may repeat, so the list keeps order and duplicates instead of a dictionary.
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseEffective(string raw)
    {
        var rows = new List<KeyValuePair<string, string>>();
        foreach (var line in raw.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0) continue;
            var space = text.IndexOf(' ');
            if (space <= 0) continue;
            rows.Add(new KeyValuePair<string, string>(text[..space], text[(space + 1)..].Trim()));
        }
        return rows;
    }

    // ---------------------------------------------------------------- ssh -vvv

    /// <summary>
    /// Turns a verbose handshake into an ordered stage list. The order is the point: it shows
    /// where a connection stopped, not just that it failed.
    /// </summary>
    public static List<Step> ParseVerbose(string log)
    {
        var steps = new List<Step>();
        if (string.IsNullOrWhiteSpace(log)) return steps;

        void Add(string stage, string detail, string state = "ok") =>
            steps.Add(new Step { Stage = stage, Detail = detail, State = state });

        foreach (var rawLine in log.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Failure lines first: several of them also match a benign pattern below.
            if (line.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
            { Add("DNS", Trim(line), "fail"); continue; }
            if (line.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            { Add("TCP", Trim(line), "fail"); continue; }
            if (line.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Operation timed out", StringComparison.OrdinalIgnoreCase))
            { Add("TCP", Trim(line), "fail"); continue; }
            if (line.Contains("no matching key exchange method", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("no matching cipher", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("no matching host key type", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("no matching MAC", StringComparison.OrdinalIgnoreCase))
            { Add("Algorithms", Trim(line), "fail"); continue; }
            if (line.Contains("kex_exchange_identification", StringComparison.OrdinalIgnoreCase))
            { Add("Banner", Trim(line), "fail"); continue; }
            if (line.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            { Add("Host key", Trim(line), "fail"); continue; }
            if (line.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            { Add("Host key", Trim(line), "fail"); continue; }
            if (line.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase))
            { Add("Authentication", Trim(line), "fail"); continue; }
            if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            { Add("Authentication", Trim(line), "fail"); continue; }

            if (line.Contains("Reading configuration data", StringComparison.OrdinalIgnoreCase))
                Add("Configuration", TrimAfter(line, "Reading configuration data"));
            else if (line.Contains("Connecting to", StringComparison.OrdinalIgnoreCase) && line.Contains(" port "))
                Add("TCP", TrimAfter(line, "Connecting to"));
            else if (line.Contains("Connection established", StringComparison.OrdinalIgnoreCase))
                Add("TCP", "Connection established");
            else if (line.Contains("Remote protocol version", StringComparison.OrdinalIgnoreCase))
                Add("Banner", TrimAfter(line, "Remote protocol version"));
            else if (line.Contains("kex: algorithm:", StringComparison.OrdinalIgnoreCase))
                Add("Key exchange", TrimAfter(line, "kex: algorithm:"));
            else if (line.Contains("Server host key:", StringComparison.OrdinalIgnoreCase))
                Add("Server host key", TrimAfter(line, "Server host key:"));
            else if (line.Contains("is known and matches", StringComparison.OrdinalIgnoreCase))
                Add("Host key", Trim(line));
            else if (line.Contains("Authentications that can continue", StringComparison.OrdinalIgnoreCase))
                Add("Auth methods", TrimAfter(line, "Authentications that can continue:"));
            else if (line.Contains("Offering public key", StringComparison.OrdinalIgnoreCase))
                Add("Offering key", TrimAfter(line, "Offering public key:"));
            else if (line.Contains("Server accepts key", StringComparison.OrdinalIgnoreCase))
                Add("Key accepted", TrimAfter(line, "Server accepts key:"));
            else if (line.Contains("Authenticated to", StringComparison.OrdinalIgnoreCase))
                Add("Authenticated", TrimAfter(line, "Authenticated to"));
            else if (line.Contains("Entering interactive session", StringComparison.OrdinalIgnoreCase))
                Add("Session", "Interactive session opened");
            else if (line.Contains("Transferred: sent", StringComparison.OrdinalIgnoreCase))
                Add("Closed", Trim(line));
        }

        return steps;
    }

    /// <summary>
    /// Hints for text the pane already showed (ssh's own error line), minus anything the probe's
    /// own hints already cover. The pane's failure is evidence even when a fresh probe succeeds —
    /// a key that was locked a minute ago, a host that was down.
    /// </summary>
    public static List<string> HintsForPane(string paneText, IReadOnlyList<string> already)
    {
        var list = new List<string>();
        foreach (var hint in HintsFor(paneText))
            if (!already.Contains(hint) && !list.Contains(hint)) list.Add(hint);
        return list;
    }

    private static string Trim(string line) => line.Length <= 240 ? line : line[..240] + "…";

    private static string TrimAfter(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var text = at < 0 ? line : line[(at + marker.Length)..].Trim().TrimStart(':').Trim();
        return Trim(text);
    }

    // ---------------------------------------------------------------- the offline error table

    /// <summary>
    /// The ~20 failures people actually hit, mapped to a next action. Deliberately local and
    /// deterministic: this is the answer a search engine would give, without the search.
    /// </summary>
    public static List<string> HintsFor(string text)
    {
        var hints = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return hints;

        void If(string pattern, params string[] items)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                foreach (var item in items)
                    if (!hints.Contains(item)) hints.Add(item);
        }

        If(@"Permission denied \(publickey", 
            "The server rejected every key offered. The effective-config tab lists the identity files ssh will actually try, in order.",
            "Check the key is loaded in the agent or named in the session, and that the remote ~/.ssh/authorized_keys holds the matching public key with 700/600 permissions.",
            "Run the probe with `ssh -vvv` and look at 'Offering public key' / 'Server accepts key' to see which key was tried.");

        If(@"Permission denied \(password",
            "Password authentication was attempted and refused. If the account uses a key, the server may have PasswordAuthentication off; if it uses a password, check the vault entry.");

        If(@"Too many authentication failures",
            "ssh offered more identities than the server allows before a password prompt. Set IdentitiesOnly=yes (the session already does when a key is set) or trim the agent.");

        If(@"Host key verification failed|REMOTE HOST IDENTIFICATION HAS CHANGED",
            "The presented host key does not match known_hosts. Compare the old and new fingerprints before deleting anything: a reinstall and a man-in-the-middle look identical to ssh.",
            "`ssh-keygen -F <host>` prints the stored entry; `ssh-keyscan <host>` prints what is offered now.");

        If(@"Could not resolve hostname",
            "DNS failed before ssh reached the network. Check the name, the search suffix, and whether this host is only reachable through a jump host or VPN.");

        If(@"Connection refused",
            "TCP was refused: something is listening on that address but not on this port, or sshd is not running. Check `ss -tlnp | grep :22` on the host.");

        If(@"Connection timed out|Operation timed out",
            "No TCP answer at all. A firewall, a wrong address, or a host that is down. Try the same host from the jump host.");

        If(@"no matching key exchange method|no matching cipher|no matching host key type|no matching MAC",
            "The client and server share no algorithm. This is common with old appliances: the fix is a per-host `KexAlgorithms +diffie-hellman-group14-sha1` or `HostKeyAlgorithms +ssh-rsa` line, not a global change.",
            "The Logs tab of this window names the algorithm ssh asked for.");

        If(@"kex_exchange_identification|Connection closed by remote host|Connection reset by peer",
            "The TCP connection opened and then died before the SSH banner. Usually a proxy, a rate limiter (fail2ban), or a port that is not ssh. Check the host's auth log.");

        If(@"Broken pipe|Connection to .* closed by remote host",
            "The session dropped mid-stream. Check `ClientAliveInterval` on the server and `ServerAliveInterval` here; a NAT idle timeout is the usual cause.");

        If(@"WARNING: UNPROTECTED PRIVATE KEY FILE|bad permissions",
            "The private key is readable by other users. ssh refuses it; `icacls` on Windows or `chmod 600` on the host fixes it.");

        If(@"No such file or directory.*identity|no such identity",
            "The configured key path does not exist. The effective-config tab shows the paths ssh resolved, including ~ expansion.");

        If(@"Connection closed by authenticating user",
            "ssh authenticated but the server closed the session: often AllowUsers/AllowGroups, a force-command, or a full disk on the host.");

        If(@"Host key for .* has changed|POSSIBLE DNS SPOOFING",
            "The stored key changed. Verify the host's fingerprint out of band, then update only that host's entry.");

        If(@"X11 forwarding request failed",
            "The server refused X11 forwarding (X11Forwarding no, or no xauth). The session stays usable; remote GUI programs will fail to open a display.");

        If(@"PTY allocation request failed",
            "The server refused a terminal (no pty, or `-T` on the far side). Interactive programs will not work, but commands will.");

        If(@"subsystem request failed|sftp: command not found",
            "The server has no sftp subsystem configured. Add `Subsystem sftp internal-sftp` to sshd_config, or use the terminal's ZMODEM path.");

        If(@"Connection reset|10054|10053",
            "A middlebox reset the connection. VPN, MTU or an intrusion-prevention device; retry from another network to confirm.");

        return hints;
    }
}
