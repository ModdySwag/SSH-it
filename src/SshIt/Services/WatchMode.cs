using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SshIt.Models;

namespace SshIt.Services;

public sealed class WatchRun
{
    public int Index { get; init; }
    public DateTime At { get; init; }
    public int Exit { get; init; }
    public TimeSpan Duration { get; init; }
    public string Output { get; init; } = "";
    public List<string> Added { get; init; } = new();
    public List<string> Removed { get; init; } = new();

    public bool Changed => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// watch(1) for a session: one ssh process runs `while :; do (command) 2>&1; print a marker with
/// the exit status; sleep N; done`, so every tick is a chunk of that stream rather than a new
/// handshake. Each chunk is diffed against the one before it, and the diff is what the window
/// highlights — "is it still down" answered without retyping the command.
///
/// The marker is a random token per watch, carried on a line that begins with 0x1E (record
/// separator), so ordinary output cannot be mistaken for the end of a run.
/// </summary>
public sealed class CommandWatch : IDisposable
{
    private readonly Session _session;
    private readonly int _intervalSeconds;
    private readonly string _token = Guid.NewGuid().ToString("n")[..12];
    private readonly List<WatchRun> _runs = new();
    private readonly List<string> _errors = new();
    private readonly object _gate = new();

    private Process? _process;
    private Thread? _reader;
    private volatile bool _stopping;

    public CommandWatch(Session session, string command, int intervalSeconds = 10)
    {
        _session = session;
        Command = command;
        _intervalSeconds = Math.Clamp(intervalSeconds, 1, 3600);
    }

    public string Command { get; }
    public int IntervalSeconds => _intervalSeconds;
    public bool Running => _process is { HasExited: false };
    public IReadOnlyList<WatchRun> Runs { get { lock (_gate) return _runs.ToList(); } }
    public string Errors { get { lock (_gate) return string.Join("\n", _errors); } }

    /// <summary>One completed tick, on a background thread.</summary>
    public event Action<WatchRun>? Ran;

    /// <summary>A status line: started, stopped, or why it stopped.</summary>
    public event Action<string>? Note;

    /// <summary>The exact remote loop, so the window can show it and a test can check it.</summary>
    public string RemoteLoop()
    {
        // st=... before printf: the status must be captured before any other command runs.
        var marker = $"printf '\\036WATCH_{_token}_%s\\n' \"$st\"";
        var body = _intervalSeconds == 1 ? "sleep 1" : $"sleep {_intervalSeconds}";
        return $"while :; do ( {Command} ) 2>&1; st=$?; {marker}; {body}; done";
    }

    public string MarkerPrefix => "\u001eWATCH_" + _token + "_";

    /// <summary>
    /// A line that carries the record separator but is not a real marker is ordinary output. It is
    /// kept, with the separator shown as a visible symbol, so nothing in `output` can impersonate a
    /// boundary later.
    /// </summary>
    public static string EscapeMarker(string line) => line.Replace('\u001e', '\u241e');

    /// <summary>
    /// True when the line carries a REAL marker: the token at any offset (a command without a
    /// trailing newline glues its output to the marker) followed by an integer and nothing else.
    /// </summary>
    public static bool TryMarker(string line, string prefix, out int at, out int exit)
    {
        at = line.IndexOf(prefix, StringComparison.Ordinal);
        exit = 0;
        return at >= 0
               && int.TryParse(line[(at + prefix.Length)..].Trim(), NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out exit);
    }

    public void Start()
    {
        if (Running) return;
        _stopping = false;

        var args = "-n -T -o BatchMode=yes -o ConnectTimeout=8 "
                   + SshLauncher.BuildBatchArguments(_session)
                   + " " + SshLauncher.QuoteArgument(RemoteLoop());

        var info = new ProcessStartInfo(SshLauncher.SshExe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            _process = Process.Start(info);
            if (_process is null) { Note?.Invoke("ssh could not be started."); return; }
        }
        catch (Exception ex)
        {
            Note?.Invoke("ssh could not be started: " + ex.Message);
            return;
        }

        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_gate)
            {
                if (_errors.Count < 40) _errors.Add(e.Data.Trim());
            }
        };
        _process.BeginErrorReadLine();

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "watch-" + _session.Id };
        _reader.Start();
        Note?.Invoke($"Watching every {_intervalSeconds}s — {Command}");
    }

    private void ReadLoop()
    {
        var process = _process;
        if (process is null) return;

        var chunk = new List<string>();
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                // Not StartsWith: the loop prints the marker with no leading newline, so a command
                // whose output has no trailing newline (`printf ok`, `echo -n`) glues it to the text
                // and the run would never complete. The marker may sit at any offset, and only
                // prefix+integer counts — a line that merely looks like one is output.
                if (TryMarker(line, MarkerPrefix, out var at, out var exit))
                {
                    if (at > 0) chunk.Add(EscapeMarker(line[..at]));
                    Complete(chunk, exit);
                    chunk = new List<string>();
                    continue;
                }
                chunk.Add(EscapeMarker(line));
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) Note?.Invoke("watch stopped reading: " + ex.Message);
        }

        // A run cut short by Stop is still a run: keep what arrived.
        if (chunk.Count > 0 && !_stopping) Complete(chunk, -1);

        if (!_stopping)
        {
            string detail;
            lock (_gate) detail = string.Join("\n", _errors).Trim();
            Note?.Invoke(detail.Length > 0
                ? "watch ended: " + FirstLine(detail)
                : $"watch ended (ssh exit {(process.HasExited ? process.ExitCode : -1)}).");
        }
    }

    private void Complete(List<string> output, int exit)
    {
        WatchRun? previous;
        WatchRun run;
        lock (_gate)
        {
            previous = _runs.Count > 0 ? _runs[^1] : null;
            var (added, removed) = previous is null
                ? (new List<string>(), new List<string>())
                : Diff(previous.Output, string.Join("\n", output));

            run = new WatchRun
            {
                Index = _runs.Count + 1,
                At = DateTime.Now,
                Exit = exit,
                Duration = previous is null ? TimeSpan.Zero : DateTime.Now - previous.At,
                Output = string.Join("\n", output),
                Added = added,
                Removed = removed
            };
            _runs.Add(run);
        }
        Ran?.Invoke(run);
    }

    public void Stop()
    {
        _stopping = true;
        var process = _process;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        try { process.Dispose(); } catch { }
        _process = null;
        Note?.Invoke("Watch stopped.");
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- diffing

    /// <summary>
    /// Line diff by multiplicity: a line that appears in both runs is unchanged, a line with more
    /// copies in the new run is added, and one with fewer is removed. Good enough to say what
    /// changed; deliberately not a positional diff, because a watch run rarely keeps line order.
    /// </summary>
    public static (List<string> Added, List<string> Removed) Diff(string before, string after)
    {
        static List<string> Lines(string text) =>
            (text ?? "").Replace("\r", "").Split('\n').ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(before))
            counts[line] = counts.TryGetValue(line, out var n) ? n + 1 : 1;

        var added = new List<string>();
        foreach (var line in Lines(after))
        {
            if (counts.TryGetValue(line, out var n) && n > 0) counts[line] = n - 1;
            else added.Add(line);
        }

        var removed = new List<string>();
        foreach (var pair in counts)
            for (var i = 0; i < pair.Value; i++) removed.Add(pair.Key);

        return (added, removed);
    }

    // ---------------------------------------------------------------- receipt

    /// <summary>
    /// Markdown written when the watch stops: what was watched, for how long, how many runs, which
    /// ones changed, and the last run's output with its changes marked. No network, no assets.
    /// </summary>
    public string Receipt()
    {
        var runs = Runs;
        var text = new StringBuilder();
        text.AppendLine("# Watch receipt");
        text.AppendLine();
        text.AppendLine("- Session: " + _session.DisplayTarget);
        text.AppendLine("- Command: `" + Command + "`");
        text.AppendLine("- Interval: " + _intervalSeconds + " s");
        text.AppendLine("- Runs: " + runs.Count + (runs.Count == 0 ? "" : " from " + runs[0].At.ToString("yyyy-MM-dd HH:mm:ss")));
        text.AppendLine("- Changed runs: " + runs.Count(r => r.Changed));
        if (runs.Count > 0)
        {
            var last = runs[^1];
            text.AppendLine("- Last run: exit " + last.Exit + " at " + last.At.ToString("HH:mm:ss"));
            text.AppendLine();
            text.AppendLine("## Last run");
            text.AppendLine();
            text.AppendLine("```");
            foreach (var line in last.Output.Split('\n').Take(400)) text.AppendLine(line);
            text.AppendLine("```");
            if (last.Changed)
            {
                text.AppendLine();
                text.AppendLine("## Changes since the run before");
                text.AppendLine();
                foreach (var line in last.Added.Take(200)) text.AppendLine("+ " + line);
                foreach (var line in last.Removed.Take(200)) text.AppendLine("- " + line);
            }
        }

        if (Errors.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## ssh diagnostics");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Errors);
            text.AppendLine("```");
        }

        return text.ToString();
    }

    private static string FirstLine(string text) =>
        text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
}
