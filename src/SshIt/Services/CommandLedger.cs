using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One finished command as the ledger remembers it: what ran, where, how it ended, and what it said.</summary>
public sealed class LedgerRow
{
    /// <summary>A sort key, not a count: the UTC ticks of the moment the row was written.</summary>
    public long Seq { get; set; }
    public string At { get; set; } = "";
    public string Session { get; set; } = "";
    public string Host { get; set; } = "";
    public string User { get; set; } = "";
    public string Command { get; set; } = "";
    public int Exit { get; set; }
    public int DurationMs { get; set; }
    public string Cwd { get; set; } = "";
    public string Output { get; set; } = "";

    /// <summary>Bytes of output before the cap, so a search hit can say how much was left out.</summary>
    public int OutputBytes { get; set; }

    public string When => DateTime.TryParse(At, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var stamp)
        ? stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : At;

    public string OneLine
    {
        get
        {
            var command = (Command ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (command.Length > 60) command = command[..59] + "…";
            return When.PadRight(16) + "  " + (Session.Length > 0 ? Session : Host).PadRight(14)
                   + "  exit " + Exit.ToString(CultureInfo.InvariantCulture).PadRight(4)
                   + "  " + (DurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5) + "s  "
                   + command;
        }
    }
}

/// <summary>
/// C9: the command ledger. Every finished block is appended with its output, so "what did I run and
/// what did it say" is answerable later — from the CLI, from the palette, and from an agent.
///
/// **Why a JSONL file and not SQLite FTS5.** This project ships no third-party packages, so SQLite
/// would mean P/Invoking winsqlite3.dll, whose FTS5 support varies by Windows build; a half-working
/// index is worse than an honest scan. The store is append-only JSONL, the search is a linear scan
/// over the last 5000 rows with case-insensitive AND tokens, and command hits weigh more than output
/// hits. If this ever needs to scale, the file is the migration source for a real index — the format
/// will not have to change.
///
/// The writer is the app (one process), the write never throws into the pane, and output is capped at
/// 8 KB (5 KB head, 3 KB tail) so a 200 MB build log cannot make the ledger unsearchable.
/// </summary>
public static class CommandLedger
{
    public const int MaxOutput = 8_000;
    public const int HeadBytes = 5_000;
    public const int TailBytes = 3_000;
    public const int ScanRows = 5_000;

    private static readonly object Gate = new();

    public static string StorePath => Path.Combine(DataStore.RootPath, "ledger.jsonl");

    // ---------------------------------------------------------------- writing

    /// <summary>Appends one finished block. Returns null for a block with nothing worth keeping.</summary>
    public static LedgerRow? Append(Session session, string? command, int exit, TimeSpan duration,
                                    string? cwd, string? output, string? path = null)
    {
        var text = (command ?? "").Trim();
        var said = output ?? "";
        if (text.Length == 0 && said.Trim().Length == 0) return null;

        var row = new LedgerRow
        {
            Seq = DateTime.UtcNow.Ticks,
            At = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Session = session.Name,
            Host = session.Host,
            User = session.User,
            Command = text,
            Exit = exit,
            DurationMs = (int)Math.Max(0, duration.TotalMilliseconds),
            Cwd = cwd ?? "",
            Output = Cap(said),
            OutputBytes = Encoding.UTF8.GetByteCount(said)
        };

        try
        {
            lock (Gate)
            {
                var file = path ?? StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, JsonSerializer.Serialize(row) + "\n", new UTF8Encoding(false));
            }
        }
        catch
        {
            // A ledger that breaks the terminal is worse than no ledger.
        }
        return row;
    }

    /// <summary>Keeps the head and the tail of a long output; the middle is what nobody greps.</summary>
    public static string Cap(string text)
    {
        if (text.Length <= MaxOutput) return text;
        var head = text[..HeadBytes];
        var tail = text[^TailBytes..];
        return head + "\n… " + (text.Length - HeadBytes - TailBytes) + " characters omitted …\n" + tail;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The newest rows, oldest first. Damaged lines are skipped, never fatal.</summary>
    public static List<LedgerRow> Tail(int limit, string? path = null, DateTime? since = null)
    {
        var rows = new List<LedgerRow>();
        var file = path ?? StorePath;
        if (!File.Exists(file)) return rows;

        try
        {
            var lines = File.ReadAllLines(file);

            // Newest first, and the limit applies AFTER the date filter: taking the newest N rows
            // and then dropping the old ones made "--since three days ago" return nothing on a
            // busy ledger, because the window never reached back that far.
            for (var i = lines.Length - 1; i >= 0 && rows.Count < limit; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    var row = JsonSerializer.Deserialize<LedgerRow>(lines[i]);
                    if (row is null) continue;
                    if (since is { } cutoff)
                    {
                        if (!DateTime.TryParse(row.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                            continue;
                        if (at.ToLocalTime() < cutoff) continue;
                    }
                    rows.Add(row);
                }
                catch { /* a damaged row is skipped; the rest is still useful */ }
            }
            rows.Reverse();
        }
        catch
        {
            // An unreadable ledger reads as an empty one.
        }
        return rows;
    }

    /// <summary>
    /// Case-insensitive AND over the query's words, searched across the command, the output, the
    /// directory and the host. Every word must appear somewhere; a word in the command scores higher
    /// than the same word only in the output, so "docker" finds the commands, not every log that
    /// mentions it.
    /// </summary>
    public static List<LedgerRow> Search(string? query, int limit = 20, string? path = null,
                                         string? host = null, string? session = null,
                                         string? exit = null, DateTime? since = null)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var phrase = words.Length > 1 ? string.Join(" ", words) : null;

        var scored = new List<(LedgerRow Row, int Score)>();
        foreach (var row in Tail(ScanRows, path))
        {
            if (host is { Length: > 0 } && !row.Host.Contains(host, StringComparison.OrdinalIgnoreCase)) continue;
            if (session is { Length: > 0 }
                && !(row.Session.Contains(session, StringComparison.OrdinalIgnoreCase)
                     || row.Host.Contains(session, StringComparison.OrdinalIgnoreCase))) continue;
            if (!ExitMatches(row.Exit, exit)) continue;
            if (since is not null)
            {
                if (!DateTime.TryParse(row.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    || at.ToLocalTime() < since.Value) continue;
            }

            var score = 0;
            var matched = true;
            foreach (var word in words)
            {
                var inCommand = (row.Command ?? "").Contains(word, StringComparison.OrdinalIgnoreCase);
                var inOutput = row.Output.Contains(word, StringComparison.OrdinalIgnoreCase);
                var inCwd = row.Cwd.Contains(word, StringComparison.OrdinalIgnoreCase);
                var inHost = row.Host.Contains(word, StringComparison.OrdinalIgnoreCase);
                if (!inCommand && !inOutput && !inCwd && !inHost) { matched = false; break; }
                if (inCommand) score += 10;
                if (inOutput) score += 4;
                if (inCwd) score += 2;
                if (inHost) score += 3;
            }
            if (!matched) continue;

            if (phrase is not null && (row.Command ?? "").Contains(phrase, StringComparison.OrdinalIgnoreCase)) score += 25;
            scored.Add((row, score));
        }

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Row.Seq)
            .Take(Math.Max(1, limit))
            .Select(pair => pair.Row)
            .ToList();
    }

    private static bool ExitMatches(int exit, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (filter.Equals("nonzero", StringComparison.OrdinalIgnoreCase)) return exit != 0;
        if (filter.Equals("zero", StringComparison.OrdinalIgnoreCase)) return exit == 0;
        return int.TryParse(filter, out var wanted) && exit == wanted;
    }

    // ---------------------------------------------------------------- text

    public static string Format(IReadOnlyList<LedgerRow> rows, bool json)
    {
        if (json) return JsonSerializer.Serialize(rows);

        if (rows.Count == 0) return "no ledger entries match." + Environment.NewLine;
        var text = new StringBuilder();
        foreach (var row in rows) text.AppendLine(row.OneLine);
        text.AppendLine("— " + rows.Count + " row(s). '--ledger <words>' searches command and output.");
        return text.ToString();
    }

    /// <summary>Deletes the store and returns how many rows it held.</summary>
    public static int Clear(string? path = null)
    {
        var file = path ?? StorePath;
        if (!File.Exists(file)) return 0;
        var count = 0;
        try
        {
            // Count rows, not lines: a damaged line was never a row anyone could search.
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<LedgerRow>(line) is not null) count++;
                }
                catch { }
            }
        }
        catch { }
        try { File.Delete(file); } catch { }
        return count;
    }
}
