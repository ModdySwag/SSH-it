using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Terminal;

namespace SshIt.Services;

/// <summary>
/// L9: one honest, repeatable measurement of the terminal core. It measures the PARSER — bytes in,
/// buffer state out — and says so; parse throughput is not a session's throughput and is never the
/// headline. The headline (keystroke to painted echo) is measured live by the Performance panel from
/// real keystrokes, because it needs a window, a shell and a paint.
/// </summary>
public static class Bench
{
    public sealed record Corpus(string Name, byte[] Data, int Checksum);

    public sealed record ParseResult(string Name, int Bytes, int Runs, int Warmup,
        double MedianMb, double P95Mb, double SigmaMb, double MinMb, double MaxMb, int Lines);

    public sealed record BenchReport(JsonObject Json, string Text);

    public static BenchReport Run(string? comparePath)
    {
        var preconditions = Preconditions();
        var corpora = new[]
        {
            Ascii("ascii", 2_000_000),
            CsiHeavy("csi-heavy", 2_000_000),
            Utf8("utf-8", 2_000_000)
        };

        var results = corpora.Select(c => Measure(c, runs: 5, warmup: 1)).ToList();

        var managed = GC.GetTotalMemory(true);
        using var self = Process.GetCurrentProcess();
        var memory = new JsonObject
        {
            ["managedMb"] = Math.Round(managed / 1048576.0, 1),
            ["workingSetMb"] = Math.Round(self.WorkingSet64 / 1048576.0, 1),
            ["privateMb"] = Math.Round(self.PrivateMemorySize64 / 1048576.0, 1),
            ["note"] = "the bench process only. Per-pane memory (app + conhost + ssh) needs live panes " +
                       "and is reported by the Performance panel's ssh-children row, not here."
        };

        var json = new JsonObject
        {
            ["schema"] = 1,
            ["tool"] = "ssh-it",
            ["version"] = typeof(Bench).Assembly.GetName().Version?.ToString() ?? "?",
            ["at"] = DateTimeOffset.Now.ToString("o"),
            ["preconditions"] = preconditions,
            ["parse"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["bytes"] = r.Bytes,
                ["runs"] = r.Runs,
                ["warmupDropped"] = r.Warmup,
                ["medianMBps"] = Round(r.MedianMb),
                ["p95MBps"] = Round(r.P95Mb),
                ["sigmaMBps"] = Round(r.SigmaMb),
                ["minMBps"] = Round(r.MinMb),
                ["maxMBps"] = Round(r.MaxMb),
                ["lines"] = r.Lines
            }).ToArray()),
            ["memory"] = memory,
            ["startup"] = new JsonObject
            {
                ["ms"] = null,
                ["note"] = "cold/warm startup is measured by the window (process start to first frame) " +
                           "and shown in the Performance panel; a headless process never paints."
            },
            ["notes"] = new JsonArray(
                "parse MB/s is the parser alone (bytes in, buffer state out), not end-to-end session throughput",
                "n=5 after one discarded warm-up; median, p95 (nearest rank) and sigma over the runs",
                "corpora are generated in-process from a fixed seed, so the byte stream and checksum are stable",
                "preconditions are asserted and labelled, not enforced: a busy machine still produces a number")
        };

        var compare = Compare.Run(comparePath, results, json);
        var text = Format(json, compare);
        return new BenchReport(json, text);
    }

    // ---------------------------------------------------------------- corpora

    /// <summary>ASCII text as a shell prints it: prompts, paths, numbers, no escape codes.</summary>
    internal static Corpus Ascii(string name, int target)
    {
        var sb = new StringBuilder(target + 256);
        var n = 0;
        while (sb.Length < target)
        {
            sb.Append("demo@example-host:").Append(20 + n % 8).Append("$ ls -la /var/log/sshit-")
              .Append(n.ToString("D4")).Append(".log\r\n");
            sb.Append("-rw-r--r-- 1 root root ").Append(4096 + n % 9000).Append(" Nov  1 23:5")
              .Append(n % 10).Append(" item-").Append(n.ToString("D6")).Append(".txt\r\n");
            n++;
        }
        var data = Encoding.ASCII.GetBytes(sb.ToString());
        return new Corpus(name, data, Checksum(data));
    }

    /// <summary>CSI-heavy: cursor addressing, erases, scroll regions, SGR — closer to vim/htop than ls.</summary>
    internal static Corpus CsiHeavy(string name, int target)
    {
        var sb = new StringBuilder(target + 256);
        var n = 0;
        while (sb.Length < target)
        {
            sb.Append("\u001b[").Append(1 + n % 40).Append(';').Append(1 + n % 120).Append('H')
              .Append("\u001b[2K\u001b[").Append(30 + n % 8).Append('m')
              .Append("row ").Append(n.ToString("D5")).Append("  value ").Append((n * 7919) % 100000)
              .Append("\u001b[0m\u001b[").Append(1 + n % 5).Append('A')
              .Append("\u001b[").Append(1 + n % 10).Append("X");
            if (n % 17 == 0) sb.Append("\u001b[1;1H\u001b[2J");
            if (n % 23 == 0) sb.Append("\u001b[5;30r\u001b[10;1H\u001b[M");
            n++;
        }
        var data = Encoding.ASCII.GetBytes(sb.ToString());
        return new Corpus(name, data, Checksum(data));
    }

    /// <summary>UTF-8: CJK, accents and an emoji, so the decoder path is exercised, not just bytes.</summary>
    internal static Corpus Utf8(string name, int target)
    {
        var sb = new StringBuilder(target + 256);
        var n = 0;
        string[] words = { "日本語のログ行", "naïve café", "数据 Data", "🦞 shellfish", "Grüße", "пример текста" };
        while (sb.Length < target)
        {
            sb.Append(words[n % words.Length]).Append(" — ").Append(n.ToString("D5")).Append(" · ")
              .Append("ok").Append("\r\n");
            n++;
        }
        var data = Encoding.UTF8.GetBytes(sb.ToString());
        return new Corpus(name, data, Checksum(data));
    }

    private static int Checksum(byte[] data)
    {
        var hash = 0;
        foreach (var b in data) hash = unchecked(hash * 31 + b);
        return hash;
    }

    // ---------------------------------------------------------------- measurement

    /// <summary>Feeds the corpus through a fresh parser+buffer, in 64 KB chunks, and times the feed.</summary>
    public static ParseResult Measure(Corpus corpus, int runs, int warmup)
    {
        var samples = new List<double>(runs);
        var lines = 0;
        for (var run = -warmup; run < runs; run++)
        {
            var buffer = new TerminalBuffer(120, 40);
            var parser = new VtParser(buffer, _ => { });
            var watch = new Stopwatch();
            const int chunk = 65536;
            watch.Start();
            for (var at = 0; at < corpus.Data.Length; at += chunk)
                parser.Feed(corpus.Data, Math.Min(chunk, corpus.Data.Length - at));
            watch.Stop();
            var mbps = corpus.Data.Length / 1_000_000.0 / watch.Elapsed.TotalSeconds;
            if (run >= 0) samples.Add(mbps);
            lines = (int)buffer.TotalLines;
        }

        samples.Sort();
        var median = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);
        var mean = samples.Average();
        var sigma = Math.Sqrt(samples.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, samples.Count));
        return new ParseResult(corpus.Name, corpus.Data.Length, runs, warmup,
            median, p95, sigma, samples[0], samples[^1], lines);
    }

    public static double Percentile(List<double> sorted, double fraction) =>
        PerfMetrics.Percentile(sorted.ToArray(), fraction);

    private static double Round(double value) => Math.Round(value, 2);

    // ---------------------------------------------------------------- preconditions

    private static JsonObject Preconditions()
    {
        var other = 0;
        try { other = Math.Max(0, Process.GetProcessesByName("SSH-it").Length - 1); } catch { }

        return new JsonObject
        {
            ["acPower"] = OnAcPower(),
            ["otherInstances"] = other,
            ["refreshHz"] = DisplayRefreshHz(),
            ["dpi"] = "n/a (headless)",
            ["font"] = "n/a (headless)",
            ["renderTier"] = "n/a (headless)",
            ["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?",
            ["os"] = Environment.OSVersion.VersionString,
            ["asserted"] = false,
            ["note"] = "values are recorded, not enforced: a battery-powered or busy machine yields a lower number"
        };
    }

    private static string OnAcPower()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status) || status.ACLineStatus == 255) return "unknown";
            return status.ACLineStatus == 1 ? "yes" : "no";
        }
        catch { return "unknown"; }
    }

    public static int DisplayRefreshHz()
    {
        try
        {
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettings(null, -1, ref mode) ? (int)mode.dmDisplayFrequency : 0;
        }
        catch { return 0; }
    }

    // ---------------------------------------------------------------- output

    private static string Format(JsonObject json, CompareResult? compare)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SSH-it bench — parser only. Not end-to-end session throughput.");
        sb.AppendLine("preconditions: " + json["preconditions"]!.ToJsonString());
        sb.AppendLine();
        sb.AppendLine("  corpus       size        median      p95       sigma     min       max");
        foreach (var row in json["parse"]!.AsArray())
        {
            sb.AppendLine($"  {row!["name"]!.GetValue<string>(),-10} " +
                          $"{row["bytes"]!.GetValue<int>() / 1000,6} KB " +
                          $"{row["medianMBps"]!.GetValue<double>(),9:0.0} " +
                          $"{row["p95MBps"]!.GetValue<double>(),9:0.0} " +
                          $"{row["sigmaMBps"]!.GetValue<double>(),9:0.0} " +
                          $"{row["minMBps"]!.GetValue<double>(),8:0.0} " +
                          $"{row["maxMBps"]!.GetValue<double>(),8:0.0}  MB/s");
        }
        sb.AppendLine();
        sb.AppendLine("  keystroke -> painted echo is measured live by Tools > Performance (real keys, a real shell).");
        if (compare is not null) sb.AppendLine().Append(compare.Text);
        return sb.ToString();
    }

    public sealed record CompareResult(string Text, bool Regressed);

    /// <summary>`--compare old.json`: a regression becomes a number, not a feeling.</summary>
    public static class Compare
    {
        public static CompareResult? Run(string? path, IReadOnlyList<ParseResult> results, JsonObject json)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!File.Exists(path)) return new CompareResult("compare: no such file: " + path, false);

            try
            {
                var old = JsonNode.Parse(File.ReadAllText(path))?["parse"]?.AsArray();
                if (old is null) return new CompareResult("compare: the file carries no parse array", false);

                var sb = new StringBuilder();
                sb.AppendLine("compare against " + Path.GetFileName(path) + ":");
                sb.AppendLine("  corpus       before      after     delta");
                var regressed = false;
                var rows = new JsonArray();
                foreach (var row in results)
                {
                    var before = old.FirstOrDefault(o =>
                        string.Equals(o?["name"]?.GetValue<string>(), row.Name, StringComparison.OrdinalIgnoreCase));
                    if (before is null) continue;
                    var oldMb = before["medianMBps"]?.GetValue<double>() ?? 0;
                    if (oldMb <= 0) continue;
                    var delta = (row.MedianMb - oldMb) * 100.0 / oldMb;
                    if (delta < -10) regressed = true;
                    sb.AppendLine($"  {row.Name,-10} {oldMb,8:0.0} {row.MedianMb,10:0.0} {delta,8:+0.0;-0.0;0.0}%");
                    rows.Add(new JsonObject
                    {
                        ["name"] = row.Name, ["beforeMBps"] = Round(oldMb),
                        ["afterMBps"] = Round(row.MedianMb), ["deltaPercent"] = Round(delta)
                    });
                }
                json["compare"] = new JsonObject
                {
                    ["file"] = Path.GetFileName(path), ["rows"] = rows,
                    ["regressed"] = regressed,
                    ["note"] = "regressed is delta < -10% on any corpus; it is a flag, not a verdict"
                };
                return new CompareResult(sb.ToString().TrimEnd(), regressed);
            }
            catch (Exception ex)
            {
                return new CompareResult("compare: could not read " + path + " — " + ex.Message, false);
            }
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
}
