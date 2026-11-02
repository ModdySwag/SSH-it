using System.Diagnostics;
using System.Windows;

namespace SshIt.Services;

/// <summary>
/// Live performance counters for the running window. UI-thread only, deliberately: every sample
/// comes from an event the UI already handles (a key, a parsed chunk, a paint, a compositor frame),
/// so the instrument cannot become the load it measures.
/// </summary>
public static class PerfMetrics
{
    private const int RingSize = 200;

    private static readonly double[] _echo = new double[RingSize];
    private static int _echoCount, _echoAt;
    private static readonly double[] _frames = new double[RingSize];
    private static int _frameCount, _frameAt;
    private static readonly double[] _render = new double[RingSize];
    private static int _renderCount, _renderAt;

    private static long _keyAt;          // Stopwatch timestamp of the newest un-echoed key
    private static bool _echoDue;        // parsed, waiting for the paint that shows it
    private static long _startedAt;      // process start, set by App.OnStartup
    private static double? _startupMs;
    private static long _lastFrameAt;

    private static long _backlog;
    private static long _parseBytes;
    private static long _parseWindowAt;
    private static long _lastParsedAt;
    private static double _parseRate;

    /// <summary>Called once, before any window exists.</summary>
    public static void MarkProcessStart()
    {
        _startedAt = Stopwatch.GetTimestamp();
        _lastFrameAt = 0;
        _startupMs = null;
    }

    /// <summary>A key the pane is about to send. Repeated keys replace the pending one: the
    /// question is "how long after the last key does its echo appear", not an average over a burst.</summary>
    public static void Key()
    {
        _keyAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Drop a pending key: a disconnect or a connect means no echo can belong to it.</summary>
    public static void ResetKey()
    {
        _keyAt = 0;
        _echoDue = false;
    }

    /// <summary>Bytes arrived from the child. Backlog approximates what the parser has not drained.</summary>
    public static void Received(int bytes)
    {
        _backlog += bytes;
        if (_backlog < 0) _backlog = 0;
    }

    /// <summary>Bytes were fed to the parser. If a key was waiting, the next paint completes it.</summary>
    public static void Parsed(int bytes)
    {
        var now = Stopwatch.GetTimestamp();
        _lastParsedAt = now;
        // A key older than two seconds did not get an echo — it got a stalled shell, a reconnect
        // or a login banner. Recording it would put a fake stall in the p99.
        if (_keyAt != 0 && (now - _keyAt) * 1000.0 / Stopwatch.Frequency > 2000)
            ResetKey();
        if (_keyAt != 0) _echoDue = true;

        _backlog -= bytes;
        if (_backlog < 0) _backlog = 0;

        if (_parseWindowAt == 0) { _parseWindowAt = now; _parseBytes = 0; }
        _parseBytes += bytes;
        var elapsed = (now - _parseWindowAt) / (double)Stopwatch.Frequency;
        if (elapsed >= 0.25)
        {
            _parseRate = _parseBytes / 1_000_000.0 / elapsed;
            _parseBytes = 0;
            _parseWindowAt = now;
        }
    }

    /// <summary>One WPF paint of the terminal surface finished.</summary>
    public static void Rendered(double milliseconds)
    {
        Add(_render, ref _renderCount, ref _renderAt, milliseconds);
        if (!_echoDue || _keyAt == 0) return;
        var ms = (Stopwatch.GetTimestamp() - _keyAt) * 1000.0 / Stopwatch.Frequency;
        _keyAt = 0;
        _echoDue = false;
        Add(_echo, ref _echoCount, ref _echoAt, ms);
    }

    /// <summary>One composer frame (CompositionTarget.Rendering).</summary>
    public static void Frame()
    {
        var now = Stopwatch.GetTimestamp();
        if (_startupMs is null && _startedAt != 0)
            _startupMs = (now - _startedAt) * 1000.0 / Stopwatch.Frequency;

        if (_lastFrameAt != 0)
        {
            var ms = (now - _lastFrameAt) * 1000.0 / Stopwatch.Frequency;
            // A frame gap longer than 2 s is the window being minimised or the machine asleep,
            // not a dropped frame; recording it would poison every percentile.
            if (ms is > 0 and < 2000) Add(_frames, ref _frameCount, ref _frameAt, ms);
        }
        _lastFrameAt = now;
    }

    private static void Add(double[] ring, ref int count, ref int at, double value)
    {
        ring[at] = value;
        at = (at + 1) % RingSize;
        if (count < RingSize) count++;
    }

    public static PerfSnapshot Snapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var echo = Stats(_echo, _echoCount);
        var frames = Stats(_frames, _frameCount);
        var render = Stats(_render, _renderCount);

        var dropped = 0;
        if (_frameCount > 1 && frames.P50 > 0)
        {
            var limit = frames.P50 * 1.5;
            for (var i = 0; i < _frameCount; i++) if (_frames[i] > limit) dropped++;
        }

        var rate = (now - _lastParsedAt) / (double)Stopwatch.Frequency > 2.0 ? 0 : _parseRate;

        long ws = 0, priv = 0;
        try
        {
            using var self = Process.GetCurrentProcess();
            ws = self.WorkingSet64;
            priv = self.PrivateMemorySize64;
        }
        catch { }

        return new PerfSnapshot
        {
            EchoCount = _echoCount, EchoP50 = echo.P50, EchoP99 = echo.P99,
            FrameCount = _frameCount, FrameP50 = frames.P50, FrameP99 = frames.P99,
            DroppedPercent = _frameCount > 1 ? dropped * 100.0 / (_frameCount - 1) : 0,
            ParseMbPerSecond = rate, BacklogBytes = _backlog,
            RenderP50 = render.P50, RenderP99 = render.P99,
            WorkingSetMb = ws / 1048576.0, PrivateMb = priv / 1048576.0,
            StartupMs = _startupMs
        };
    }

    private static (double P50, double P99) Stats(double[] ring, int count)
    {
        if (count == 0) return (0, 0);
        var copy = new double[count];
        Array.Copy(ring, copy, count);
        Array.Sort(copy);
        return (Percentile(copy, 0.50), Percentile(copy, 0.99));
    }

    /// <summary>Nearest-rank percentile on a sorted array. n=1 answers its only sample.</summary>
    public static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Child processes a terminal spawns (ssh/ConHost) are real memory; the panel asks
    /// the window, which knows its panes, rather than guessing here.</summary>
    public static Func<(int Count, long Bytes)>? Children { get; set; }
}

public sealed class PerfSnapshot
{
    public int EchoCount { get; init; }
    public double EchoP50 { get; init; }
    public double EchoP99 { get; init; }
    public int FrameCount { get; init; }
    public double FrameP50 { get; init; }
    public double FrameP99 { get; init; }
    public double DroppedPercent { get; init; }
    public double ParseMbPerSecond { get; init; }
    public long BacklogBytes { get; init; }
    public double RenderP50 { get; init; }
    public double RenderP99 { get; init; }
    public double WorkingSetMb { get; init; }
    public double PrivateMb { get; init; }
    public double? StartupMs { get; init; }
}
