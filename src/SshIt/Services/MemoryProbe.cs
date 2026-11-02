using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace SshIt.Services;

/// <summary>
/// Headless per-pane memory: starts real ConPTY panes, then samples this process, the conhost /
/// OpenConsole host each pty spawns and the shell inside it. `--memory-test [panes]` (default 4).
///
/// The Performance panel can only count the pty children it knows about; a terminal also pays for a
/// console host per pty, which is invisible from the pane list. This probe measures the whole tree by
/// difference: the console hosts that did not exist before the first pane are the panes' hosts.
/// </summary>
public static class MemoryProbe
{
    public static bool Wants(string[] args) =>
        args.Any(a => a.Equals("--memory-test", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var max = 4;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--memory-test", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var asked))
                max = Math.Clamp(asked, 1, 8);

        var shell = LocalShell();
        var hostsBefore = HostIds();
        var shellsBefore = ShellIds();
        var panes = new List<ConPty>();
        var samples = new List<PaneSample>();

        var baseline = Sample(0, hostsBefore, shellsBefore);
        try
        {
            for (var count = 1; count <= max; count++)
            {
                var pty = new ConPty();
                pty.DataReceived += (_, _) => { };   // drain; otherwise prompts land on our stdout
                pty.Start(shell, shell.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase) ? "-NoLogo" : "-NoLogo -NoExit", 80, 24);
                panes.Add(pty);
                Thread.Sleep(1200);
                var sample = Sample(count, hostsBefore, shellsBefore);
                samples.Add(sample with { DeltaMb = Math.Round(sample.TotalMb - baseline.TotalMb, 1) });
            }
        }
        finally
        {
            foreach (var pty in panes) { try { pty.Dispose(); } catch { } }
        }

        var json = new JsonObject
        {
            ["schema"] = 1,
            ["tool"] = "ssh-it",
            ["version"] = typeof(MemoryProbe).Assembly.GetName().Version?.ToString() ?? "?",
            ["at"] = DateTimeOffset.Now.ToString("o"),
            ["shell"] = shell,
            ["method"] = "real ConPTY panes; totals include this process + conhost/OpenConsole hosts "
                         + "that appeared after the baseline + each pty's shell",
            ["baseline"] = baseline.ToJson(),
            ["samples"] = new JsonArray(samples.Select(s => (JsonNode)s.ToJson()).ToArray()),
            ["limits"] = new JsonArray(
                "console hosts are attributed by name set difference, not by parent PID",
                "working set is sampled once per step after a 1.2 s settle; it is not a peak",
                "numbers are machine-specific and are meant to be compared with the same probe")
        };

        Console.WriteLine(json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private sealed record PaneSample(int Panes, double AppMb, double HostMb, double ShellMb, double TotalMb)
    {
        public double DeltaMb { get; init; }
        public double MarginalMb => Panes == 0 ? 0 : Math.Round(DeltaMb / Panes, 1);

        public JsonObject ToJson() => new()
        {
            ["panes"] = Panes,
            ["appMb"] = AppMb,
            ["consoleHostMb"] = HostMb,
            ["shellMb"] = ShellMb,
            ["totalMb"] = TotalMb,
            ["deltaMb"] = DeltaMb,
            ["marginalPerPaneMb"] = MarginalMb
        };
    }

    private static PaneSample Sample(int panes, HashSet<int> hostsBefore, HashSet<int> shellsBefore)
    {
        double ws(Process p) { try { return p.WorkingSet64; } catch { return 0; } }

        var app = 0.0;
        using (var self = Process.GetCurrentProcess()) app = ws(self);

        var hosts = 0.0;
        foreach (var name in new[] { "conhost", "OpenConsole" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (hostsBefore.Contains(process.Id)) continue;
                    hosts += ws(process);
                }
            }
        }

        // Attribute shells by name set difference too: ConPty.ProcessId is not reliable for every
        // host, and the probe starts no other pwsh/powershell processes.
        var shells = 0.0;
        foreach (var name in new[] { "pwsh", "powershell" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (shellsBefore.Contains(process.Id)) continue;
                    shells += ws(process);
                }
            }
        }

        return new PaneSample(panes, Mb(app), Mb(hosts), Mb(shells), Mb(app + hosts + shells));
    }

    private static double Mb(double bytes) => Math.Round(bytes / 1048576.0, 1);

    private static HashSet<int> ShellIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in new[] { "pwsh", "powershell" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) ids.Add(process.Id);
        return ids;
    }

    private static HashSet<int> HostIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in new[] { "conhost", "OpenConsole" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) ids.Add(process.Id);
        return ids;
    }

    private static string LocalShell()
    {
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) return pwsh;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}
