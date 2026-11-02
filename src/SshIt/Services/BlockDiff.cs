using System.Diagnostics;
using System.Text;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One aligned row of a side-by-side diff.</summary>
public sealed class DiffRow
{
    public string Kind { get; init; } = "same";   // same | change | add | remove
    public int LeftNo { get; init; }
    public int RightNo { get; init; }
    public string Left { get; init; } = "";
    public string Right { get; init; } = "";

    /// <summary>The glyph the window shows in its delta column.</summary>
    public string Mark => Kind switch
    {
        "add" => "+",
        "remove" => "−",
        "change" => "≠",
        _ => "="
    };
}

public sealed class CommandRun
{
    public string Target { get; init; } = "";
    public int Exit { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public long Milliseconds { get; init; }
}

/// <summary>
/// R3-10: run the same command twice — on one host, or on two — and align the outputs line by
/// line. Used for `show running-config` before and after a change, `df -h` across two boxes, or
/// "did my fix work" on the same host a minute later.
///
/// Alignment is an LCS over lines with a positional pass afterwards: a run of removed lines paired
/// with a run of added lines becomes "changed" rows, which is what a side-by-side view should show.
/// Very large outputs are capped (head and tail kept) so the DP table stays small and honest.
/// </summary>
public static class BlockDiff
{
    public const int MaxLines = 1200;

    /// <summary>One-shot command through the user's own ssh: no pty, no stdin, BatchMode.</summary>
    public static async Task<CommandRun> RunAsync(Session session, string command, CancellationToken token)
    {
        var started = Environment.TickCount64;
        var args = "-n -T -o BatchMode=yes -o ConnectTimeout=8 "
                   + SshLauncher.BuildBatchArguments(session)
                   + " " + SshLauncher.QuoteArgument(command);

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
            using var process = Process.Start(info);
            if (process is null)
                return new CommandRun { Target = session.DisplayTarget, Exit = -1, Error = "ssh did not start" };

            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            return new CommandRun
            {
                Target = session.DisplayTarget,
                Exit = process.ExitCode,
                Output = (await stdout.ConfigureAwait(false)).Replace("\r\n", "\n"),
                Error = (await stderr.ConfigureAwait(false)).Replace("\r\n", "\n"),
                Milliseconds = Environment.TickCount64 - started
            };
        }
        catch (OperationCanceledException)
        {
            return new CommandRun { Target = session.DisplayTarget, Exit = -1, Error = "timed out" };
        }
        catch (Exception ex)
        {
            return new CommandRun { Target = session.DisplayTarget, Exit = -1, Error = ex.Message };
        }
    }

    /// <summary>Aligned rows from two outputs. Deterministic: the same pair always gives the same rows.</summary>
    public static List<DiffRow> Align(string before, string after)
    {
        var left = Cap(Lines(before));
        var right = Cap(Lines(after));
        var rows = new List<DiffRow>();

        var m = left.Count;
        var n = right.Count;
        var table = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
        {
            for (var j = n - 1; j >= 0; j--)
            {
                table[i, j] = left[i] == right[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var i2 = 0;
        var j2 = 0;
        var pendingRemoved = new List<(int No, string Text)>();
        var pendingAdded = new List<(int No, string Text)>();

        void Flush()
        {
            var pairs = Math.Min(pendingRemoved.Count, pendingAdded.Count);
            for (var k = 0; k < pairs; k++)
            {
                rows.Add(new DiffRow
                {
                    Kind = "change",
                    LeftNo = pendingRemoved[k].No,
                    RightNo = pendingAdded[k].No,
                    Left = pendingRemoved[k].Text,
                    Right = pendingAdded[k].Text
                });
            }
            for (var k = pairs; k < pendingRemoved.Count; k++)
                rows.Add(new DiffRow { Kind = "remove", LeftNo = pendingRemoved[k].No, Left = pendingRemoved[k].Text });
            for (var k = pairs; k < pendingAdded.Count; k++)
                rows.Add(new DiffRow { Kind = "add", RightNo = pendingAdded[k].No, Right = pendingAdded[k].Text });

            pendingRemoved.Clear();
            pendingAdded.Clear();
        }

        while (i2 < m && j2 < n)
        {
            if (left[i2] == right[j2])
            {
                Flush();
                rows.Add(new DiffRow { Kind = "same", LeftNo = i2 + 1, RightNo = j2 + 1, Left = left[i2], Right = right[j2] });
                i2++;
                j2++;
            }
            else if (table[i2 + 1, j2] >= table[i2, j2 + 1])
            {
                pendingRemoved.Add((i2 + 1, left[i2]));
                i2++;
            }
            else
            {
                pendingAdded.Add((j2 + 1, right[j2]));
                j2++;
            }
        }
        while (i2 < m) { pendingRemoved.Add((i2 + 1, left[i2])); i2++; }
        while (j2 < n) { pendingAdded.Add((j2 + 1, right[j2])); j2++; }
        Flush();

        return rows;
    }

    public static int Changed(List<DiffRow> rows) => rows.Count(r => r.Kind != "same");

    /// <summary>
    /// The one-line verdict. A diff of two failed commands is not a diff: two empty outputs read as
    /// "identical" when the command never ran, which is exactly the wrong reassurance.
    /// </summary>
    public static string Verdict(CommandRun left, CommandRun right, List<DiffRow> rows)
    {
        if (left.Exit == 0 && right.Exit == 0) return Summary(rows);
        var sides = left.Exit != 0 && right.Exit != 0
            ? $"both sides failed (exit {left.Exit} / {right.Exit})"
            : left.Exit != 0
                ? $"{left.Target} failed (exit {left.Exit})"
                : $"{right.Target} failed (exit {right.Exit})";
        return sides + " — this is not a comparison";
    }

    public static string Summary(List<DiffRow> rows)
    {
        var changed = Changed(rows);
        return changed == 0
            ? $"{rows.Count} line(s), identical"
            : $"{rows.Count} line(s), {changed} changed "
              + $"({rows.Count(r => r.Kind == "change")} replaced, "
              + $"{rows.Count(r => r.Kind == "add")} added, "
              + $"{rows.Count(r => r.Kind == "remove")} removed)";
    }

    /// <summary>Plain side-by-side text, for the CLI and for a ticket.</summary>
    public static string ToText(string leftName, string rightName, List<DiffRow> rows)
    {
        var text = new StringBuilder();
        text.AppendLine($"{"",2} {leftName,-40} | {rightName}");
        foreach (var row in rows)
        {
            text.AppendLine($"{row.Mark,2} {Cut(row.Left, 40),-40} | {Cut(row.Right, 40)}");
        }
        return text.ToString().TrimEnd();
    }

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";

    private static List<string> Lines(string text) =>
        (text ?? "").Replace("\r", "").Split('\n').ToList();

    /// <summary>Head and tail with a marker in the middle, so a 10k-line config still aligns fast.</summary>
    private static List<string> Cap(List<string> lines)
    {
        if (lines.Count <= MaxLines) return lines;
        var half = MaxLines / 2;
        var capped = lines.Take(half).ToList();
        capped.Add($"… {lines.Count - MaxLines} line(s) omitted …");
        capped.AddRange(lines.Skip(lines.Count - (MaxLines - half - 1)));
        return capped;
    }
}
