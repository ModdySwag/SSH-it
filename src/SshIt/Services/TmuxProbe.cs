using System.Diagnostics;
using System.Globalization;
using System.Text;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Headless regression rig for tmux control mode: starts a real server, attaches over a RAW pipe and
/// drives the protocol end to end.
///
///   SSH-it.exe --tmux-test wsl                   tmux 3.6 inside WSL Ubuntu (the local target)
///   SSH-it.exe --tmux-test ssh &lt;session&gt; [name]  the production transport: ssh -tt host tmux -C attach
///
/// The ssh path needs a host with tmux. Both are real now: a scratch sshd inside WSL Ubuntu and the
/// a Debian 13 host (tmux 3.5a) over a private network, each 6/6. The local target stays the default
/// because it needs no host at all.
/// </summary>
public static class TmuxProbe
{
    public static bool Wants(string[] args) =>
        args.Any(a => a.Equals("--tmux-test", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        try { return RunCore(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tmux test failed: " + ex.Message);
            return 2;
        }
    }

    private static int RunCore(string[] args)
    {
        var target = ArgAt(args, "--tmux-test", 1) ?? "";
        if (target.Equals("wsl", StringComparison.OrdinalIgnoreCase)) return RunWsl();
        if (target.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            return RunSsh(ArgAt(args, "--tmux-test", 2), ArgAt(args, "--tmux-test", 3));

        Usage();
        return 2;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: --tmux-test wsl");
        Console.Error.WriteLine("       --tmux-test ssh <session id or name> [tmux session name]");
        Console.Error.WriteLine("  Starts a real tmux session, attaches in control mode and checks six behaviours.");
    }

    private static int RunWsl()
    {
        var version = TmuxVersion.ProbeAsync(null, "Ubuntu").GetAwaiter().GetResult();
        if (version is { } tooOld && !TmuxVersion.IsSupported(tooOld))
        {
            Console.Error.WriteLine(TmuxVersion.Refusal(tooOld, "WSL Ubuntu"));
            return 1;
        }

        var name = "sshit-probe-" + Guid.NewGuid().ToString("n")[..6];
        // A real shell, not `sleep`: the point is that keystrokes reach a pane and its output returns.
        if (!Wsl("tmux new-session -d -s " + name, out var startError))
        {
            Console.Error.WriteLine("could not start a tmux session: " + startError);
            return 1;
        }

        var psi = new ProcessStartInfo("wsl.exe");
        foreach (var argument in new[] { "-d", "Ubuntu", "-e", "tmux", "-C", "attach", "-t", name })
            psi.ArgumentList.Add(argument);

        using var session = TmuxControlSession.Start(psi);
        session.Begin();
        var failures = Checks(session, name);

        Wsl("tmux kill-session -t " + name, out _);
        Console.WriteLine(failures == 0 ? "RESULT: tmux control mode OK" : "RESULT: " + failures + " failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static int Checks(TmuxControlSession session, string name)
    {
        var failures = 0;

        // 1. A command's response arrives between %begin and %end and is attached to the %end.
        var from = session.Count;
        session.Send("list-sessions");
        var listed = session.WaitFor(e => e.Kind == TmuxEventKind.End && e.Text.Contains(name, StringComparison.Ordinal), 8000, out var end, from);
        Report("a command's response is paired with its %end", listed, end?.Text ?? "no %end");
        if (!listed) failures++;

        // Pane ids are GLOBAL (%3 here, because earlier sessions used %0..%2), never window-relative.
        from = session.Count;
        session.Send("list-panes -t " + name + " -F '#{pane_id}'");
        var paneKnown = session.WaitFor(e => e.Kind == TmuxEventKind.End && e.Text.Contains('%'), 8000, out var paneEnd, from);
        // %0 is a valid pane id — a fresh server's first pane. The sentinel must be -1, not 0.
        var paneId = -1;
        if (paneKnown) int.TryParse((paneEnd!.Text.Trim().TrimStart('%')), out paneId);
        Report("the pane id is discovered, not assumed", paneKnown && paneId >= 0, paneEnd?.Text ?? "no pane id");
        if (!paneKnown || paneId < 0) failures++;

        // 2. Typing through send-keys -H reaches the pane and comes back as %output. The command is
        // chosen so its RESULT cannot be confused with the echo of what was typed.
        from = session.Count;
        session.Send(TmuxControlParser.SendKeysHex(paneId, "printf 'RESULT-%s\\n' 42\r"));
        var echoed = session.WaitFor(e => e.Kind == TmuxEventKind.Output && e.Text.Contains("RESULT-42", StringComparison.Ordinal), 8000, out var output, from);
        Report("send-keys -H reaches the pane and its output returns", echoed,
            (output?.Text ?? "no output").Replace("\r", "\\r").Replace("\n", "\\n"));
        if (!echoed) failures++;

        // 3. A format query proves the client can read structured answers, not just output.
        from = session.Count;
        session.Send("display-message -p '#{pane_width}x#{pane_height}'");
        var sized = session.WaitFor(e => e.Kind == TmuxEventKind.End && e.Text.Contains("x", StringComparison.Ordinal), 8000, out var size, from);
        Report("a format query answers with the client's size", sized, size?.Text ?? "no answer");
        if (!sized) failures++;

        // 4. The layout event gives the pane grid a size tree.
        var layoutText = "";
        from = session.Count;
        session.Send("display-message -p '#{window_layout}'");
        if (session.WaitFor(e => e.Kind == TmuxEventKind.End && e.Text.Contains("x", StringComparison.Ordinal), 8000, out var layoutEnd, from))
            layoutText = layoutEnd?.Text ?? "";
        var layoutOk = false;
        TmuxLayout? layout = null;
        try { layout = TmuxLayout.Parse(layoutText.Trim()); layoutOk = layout.Leaves().Any(); }
        catch (Exception ex) { layoutText = layoutText + " (" + ex.Message + ")"; }
        Report("the window layout parses into panes", layoutOk,
            layoutOk ? string.Join(",", layout!.Leaves().Select(l => l.PaneId + ":" + l.Width + "x" + l.Height)) : layoutText);
        if (!layoutOk) failures++;

        // 5. Detach ends the control stream with %exit.
        from = session.Count;
        session.Send(TmuxControlParser.Detach);
        var exited = session.WaitFor(e => e.Kind == TmuxEventKind.Exit, 5000, out _, from);
        Report("detach-client ends the stream with %exit", exited, exited ? "%exit" : "no %exit");
        if (!exited) failures++;

        return failures;
    }

    // ---------------------------------------------------------------- ssh (the production transport)

    private static int RunSsh(string? selector, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            Usage();
            return 2;
        }

        var store = new DataStore();
        store.Load();
        var session = store.Sessions.FirstOrDefault(s =>
            s.Id.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || s.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || s.DisplayTarget.Equals(selector, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            Console.Error.WriteLine($"no session matches '{selector}' (run --sessions to list them)");
            return 2;
        }
        if (session.Kind is "Serial" or "Telnet")
        {
            Console.Error.WriteLine($"'{session.Name}' is a {session.Kind} session; the ssh transport needs an SSH host");
            return 2;
        }

        var remoteVersion = TmuxVersion.ProbeAsync(session, null).GetAwaiter().GetResult();
        if (remoteVersion is { } old && !TmuxVersion.IsSupported(old))
        {
            Console.Error.WriteLine(TmuxVersion.Refusal(old, session.DisplayTarget));
            return 1;
        }

        var name = string.IsNullOrWhiteSpace(requestedName)
            ? "sshit-probe-" + Guid.NewGuid().ToString("n")[..6]
            : requestedName!;
        var secret = CredentialVault.GetSessionSecret(session.Id);
        var askPass = string.IsNullOrEmpty(secret) ? null : CredentialVault.StoreAskPass(session.Id, secret);
        var env = SshLauncher.BuildEnvironment(session, askPass);

        Console.WriteLine($"starting tmux '{name}' on {session.DisplayTarget}...");
        if (!Remote(session, env, "tmux new-session -d -s " + RemoteQuote(name), out var startError))
        {
            Console.Error.WriteLine("could not start a tmux session over ssh: " + startError);
            return 1;
        }

        var psi = new ProcessStartInfo(SshLauncher.SshExe)
        {
            Arguments = SshLauncher.BuildTmuxArguments(session, name)
        };
        foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

        using var control = TmuxControlSession.Start(psi);
        control.Begin();
        var failures = Checks(control, name);

        Remote(session, env, "tmux kill-session -t " + RemoteQuote(name), out _);
        Console.WriteLine(failures == 0 ? "RESULT: tmux control mode OK over ssh" : "RESULT: " + failures + " failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static string RemoteQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static bool Remote(Session session, Dictionary<string, string> env, string command, out string error)
    {
        var psi = new ProcessStartInfo(SshLauncher.SshExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = SshLauncher.BuildCommandArguments(session, command)
        };
        foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(40000))
        {
            try { process.Kill(true); } catch { }
            error = "ssh did not finish within 40 s";
            return false;
        }
        error = stderr.Trim();
        return process.ExitCode == 0;
    }

    private static void Report(string what, bool ok, string detail)
        => Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + (ok ? "" : "  (" + detail + ")"));

    private static bool Wsl(string command, out string error)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-d", "Ubuntu", "-e", "bash", "-lc", command }) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        error = stderr.Trim();
        return process.ExitCode == 0;
    }

    private static string? ArgAt(string[] args, string flag, int offset)
    {
        for (var i = 0; i < args.Length; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return i + offset < args.Length ? args[i + offset] : null;
        return null;
    }
}
