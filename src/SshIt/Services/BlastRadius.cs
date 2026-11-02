using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One thing the command would do, and to what.</summary>
public sealed class BlastFinding
{
    public string Severity { get; init; } = "medium";   // high | medium
    public string Kind { get; init; } = "";              // delete, move, chmod, overwrite, format…
    public string Detail { get; init; } = "";
    public List<string> Operands { get; init; } = new();
}

/// <summary>How one operand actually expands on the host right now.</summary>
public sealed class BlastMatch
{
    public string Pattern { get; init; } = "";
    public int Count { get; init; }
    public List<string> Files { get; init; } = new();
    public List<string> Directories { get; init; } = new();
    public bool Truncated { get; init; }
}

public sealed class BlastReport
{
    public string Command { get; init; } = "";
    public string Target { get; init; } = "";
    public List<BlastFinding> Findings { get; init; } = new();
    public List<BlastMatch> Matches { get; init; } = new();
    public string? FileSystem { get; init; }
    public string? Undo { get; init; }
    public string? Error { get; init; }

    public string Verdict => Findings.Any(f => f.Severity == "high") ? "destructive"
        : Findings.Count > 0 ? "caution"
        : "clear";

    public int TotalItems => Matches.Sum(m => m.Count);
    public int TotalDirectories => Matches.Sum(m => m.Directories.Count);
}

/// <summary>
/// R3-19: what would this command touch, before it runs. The classifier is local and explains
/// itself; the expansion is one read-only command on the host, so `*` is answered by the shell that
/// would have expanded it rather than guessed at here.
///
/// Honest limits: a preview is a snapshot, not a promise — anything can change between the preview
/// and the run — and glob expansion word-splits, so a filename with spaces is counted as several
/// words. Both are printed in the window.
/// </summary>
public static class BlastRadius
{
    private static readonly Regex HighRisk = new(@"\b(rm|shred|wipefs|mkfs(\.[a-z0-9]+)?|fdisk|parted)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<BlastFinding> Assess(string command)
    {
        var findings = new List<BlastFinding>();
        var text = (command ?? "").Trim();
        if (text.Length == 0) return findings;

        var tokens = Tokenise(text);
        if (tokens.Count == 0) return findings;
        var words = tokens.Select(t => t.Trim('\'', '"')).ToList();

        // Skip a leading sudo/env assignment so the verb is found where a person would look.
        var start = 0;
        while (start < words.Count &&
               (words[start] == "sudo" || words[start] == "doas" || words[start].Contains('='))) start++;
        if (start >= words.Count) return findings;
        var verb = Path.GetFileNameWithoutExtension(words[start]).ToLowerInvariant();
        var rest = words.Skip(start + 1).ToList();

        var flags = rest.Where(w => w.StartsWith('-')).ToList();
        var operands = rest.Where(w => !w.StartsWith('-')).ToList();
        var recursive = flags.Any(f => f.Contains('r') || f.Contains("recursive"));

        switch (verb)
        {
            case "rm":
                findings.Add(new BlastFinding
                {
                    Severity = "high",
                    Kind = "delete",
                    Detail = recursive
                        ? "rm -r deletes every matched file AND directory, recursively."
                        : "rm deletes the matched files. There is no recycle bin on the host.",
                    Operands = operands
                });
                break;

            case "shred":
            case "wipefs":
                findings.Add(new BlastFinding
                {
                    Severity = "high", Kind = "erase",
                    Detail = verb + " overwrites data in place; nothing can restore it.",
                    Operands = operands
                });
                break;

            case "mkfs" or "fdisk" or "parted":
            case var v when v.StartsWith("mkfs."):
                findings.Add(new BlastFinding
                {
                    Severity = "high", Kind = "format",
                    Detail = "This writes a new filesystem or partition table over the device.",
                    Operands = operands
                });
                break;

            case "dd":
                findings.Add(new BlastFinding
                {
                    Severity = "high", Kind = "overwrite",
                    Detail = "dd writes raw blocks to its of= target, truncating whatever is there.",
                    Operands = rest.Where(w => w.StartsWith("of=", StringComparison.Ordinal))
                                   .Select(w => w[3..]).ToList()
                });
                break;

            case "truncate":
                findings.Add(new BlastFinding
                {
                    Severity = "high", Kind = "truncate",
                    Detail = "truncate changes a file's length; shrinking it discards data.",
                    Operands = operands
                });
                break;

            case "mv":
                findings.Add(new BlastFinding
                {
                    Severity = "medium", Kind = "move",
                    Detail = "mv replaces the destination if it exists, and the move is not undoable.",
                    Operands = operands
                });
                break;

            case "chmod" or "chown" or "chgrp":
                findings.Add(new BlastFinding
                {
                    Severity = recursive ? "high" : "medium",
                    Kind = verb,
                    Detail = recursive
                        ? $"{verb} -R changes every file under the paths, including ones you did not think about."
                        : $"{verb} changes the matched entries.",
                    Operands = operands
                });
                break;

            case "find":
                if (words.Any(w => w is "-delete" or "-exec") || text.Contains("-delete", StringComparison.Ordinal))
                    findings.Add(new BlastFinding
                    {
                        Severity = "high", Kind = "delete",
                        Detail = "find with -delete (or -exec rm) removes every match under the start path.",
                        Operands = operands.Take(1).ToList()
                    });
                break;

            case "git":
                if (rest.Contains("reset") && rest.Contains("--hard"))
                    findings.Add(new BlastFinding
                    {
                        Severity = "medium", Kind = "discard",
                        Detail = "git reset --hard discards uncommitted work in the working tree.",
                        Operands = new List<string>()
                    });
                else if (rest.Contains("clean") && rest.Any(f => f.Contains('f') && f.StartsWith('-')))
                    findings.Add(new BlastFinding
                    {
                        Severity = "medium", Kind = "discard",
                        Detail = "git clean -f deletes untracked files and directories.",
                        Operands = rest.Where(w => !w.StartsWith('-')).ToList()
                    });
                break;

            case "systemctl":
                if (rest.Any(w => w is "stop" or "disable" or "mask"))
                    findings.Add(new BlastFinding
                    {
                        Severity = "medium", Kind = "service",
                        Detail = "Stopping or disabling a unit can take a service (or the host's access) down.",
                        Operands = rest.Where(w => !w.StartsWith('-') && w is not ("stop" or "disable" or "mask")).ToList()
                    });
                break;
        }

        // A single '>' truncates its target before the command runs; '>>' only appends.
        if (Regex.IsMatch(text, @"(?<![0-9&>])>(?!>)") && !text.Contains("2>&1"))
        {
            var target = Regex.Match(text, @"(?<![0-9&>])>(?!>)\s*([^\s|;&]+)");
            if (target.Success)
                findings.Add(new BlastFinding
                {
                    Severity = "medium", Kind = "overwrite",
                    Detail = "> truncates the target file before the command writes anything.",
                    Operands = new List<string> { target.Groups[1].Value }
                });
        }

        return findings;
    }

    /// <summary>Every operand the findings would touch, de-duplicated, in order.</summary>
    public static List<string> Operands(List<BlastFinding> findings) =>
        findings.SelectMany(f => f.Operands)
                .Where(o => o.Length > 0 && o != "-")
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// Expands each operand on the host with the shell that would have done it, bounded to 40
    /// entries per pattern plus the true count, and asks the filesystem what a snapshot would cost.
    /// </summary>
    public static async Task<BlastReport> PreviewAsync(Session session, string command, CancellationToken token)
    {
        var findings = Assess(command);
        var report = new BlastReport { Command = command, Target = session.DisplayTarget, Findings = findings };
        var patterns = Operands(findings);
        if (patterns.Count == 0) return report;

        var script = Script(patterns);
        var args = "-n -T -o BatchMode=yes -o ConnectTimeout=8 "
                   + SshLauncher.BuildBatchArguments(session) + " "
                   + SshLauncher.QuoteArgument(script);

        var info = new System.Diagnostics.ProcessStartInfo(SshLauncher.SshExe, args)
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
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null) return Clone(report, "ssh did not start");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = (await stderr.ConfigureAwait(false)).Trim();

            ParseExpansion(output, patterns, report);

            var fileSystem = await FileSystemOfAsync(session, patterns[0], token).ConfigureAwait(false);
            return new BlastReport
            {
                Command = report.Command, Target = report.Target, Findings = report.Findings,
                Matches = report.Matches, FileSystem = fileSystem, Undo = UndoHint(fileSystem, patterns[0]),
                Error = error.Length > 0 ? error : null
            };
        }
        catch (OperationCanceledException)
        {
            return Clone(report, "timed out");
        }
        catch (Exception ex)
        {
            return Clone(report, ex.Message);
        }
    }

    /// <summary>The remote expansion, as one argument. Globs stay live; only shell metacharacters are escaped.</summary>
    public static string Script(IReadOnlyList<string> patterns)
    {
        var text = new StringBuilder();
        text.Append("i=0; ");
        foreach (var pattern in patterns)
        {
            text.Append("i=$((i+1)); printf 'P %s\\n' \"$i\"; n=0; ");
            text.Append("for f in ").Append(EscapeGlob(pattern)).Append("; do ");
            text.Append("[ -e \"$f\" ] || continue; n=$((n+1)); ");
            text.Append("if [ \"$n\" -le 40 ]; then if [ -d \"$f\" ]; then printf 'D %s\\n' \"$f\"; ");
            text.Append("else printf 'F %s\\n' \"$f\"; fi; fi; done; ");
            text.Append("printf 'N %s %s\\n' \"$i\" \"$n\"; ");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Leaves glob characters live and backslash-escapes everything else that could end a word or
    /// start a command. Quoting the whole pattern (the obvious version) makes `*` a literal, so
    /// every expansion answers "0 matches" — which reads exactly like "nothing would be deleted".
    /// </summary>
    public static string EscapeGlob(string pattern)
    {
        const string live = "*?[]{}~/.-_+=:@%#,";
        var text = new StringBuilder();
        foreach (var c in pattern ?? "")
        {
            if (char.IsLetterOrDigit(c) || live.IndexOf(c) >= 0) text.Append(c);
            else text.Append('\\').Append(c);
        }
        return text.ToString();
    }

    private static string Quote(string pattern) => "'" + pattern.Replace("'", "'\\''") + "'";

    public static void ParseExpansion(string output, IReadOnlyList<string> patterns, BlastReport report)
    {
        var byIndex = new Dictionary<int, BlastMatch>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;
            var kind = line[0];
            var body = line[2..];
            if (kind == 'P' && int.TryParse(body, out var index))
            {
                byIndex[index] = new BlastMatch { Pattern = index - 1 < patterns.Count ? patterns[index - 1] : body };
            }
            else if (kind == 'D' || kind == 'F')
            {
                var match = byIndex.Values.LastOrDefault();
                if (match is null) continue;
                if (kind == 'D') match.Directories.Add(body); else match.Files.Add(body);
            }
            else if (kind == 'N')
            {
                var parts = body.Split(' ', 2);
                if (parts.Length == 2 && int.TryParse(parts[1], out var count) &&
                    int.TryParse(parts[0], out var index2) && byIndex.TryGetValue(index2, out var match))
                {
                    report.Matches.Add(new BlastMatch
                    {
                        Pattern = match.Pattern,
                        Count = count,
                        Files = match.Files,
                        Directories = match.Directories,
                        Truncated = count > match.Files.Count + match.Directories.Count
                    });
                }
            }
        }
    }

    public static async Task<string?> FileSystemOfAsync(Session session, string path, CancellationToken token)
    {
        var command = "findmnt -no FSTYPE -T " + Quote(path) + " 2>/dev/null || stat -f -c %T " + Quote(path) + " 2>/dev/null";
        var run = await BlockDiff.RunAsync(session, command, token).ConfigureAwait(false);
        var text = run.Output.Trim();
        return text.Length == 0 ? null : text.Split('\n')[0].Trim();
    }

    public static string? UndoHint(string? fileSystem, string path)
    {
        var fs = (fileSystem ?? "").ToLowerInvariant();
        var quoted = Quote(path);
        if (fs.Contains("btrfs"))
            return "Snapshot first: btrfs subvolume snapshot -r " + quoted + " " + quoted + "/.sshit-snapshot-$(date +%s)";
        if (fs.Contains("zfs"))
            return "Snapshot first: zfs snapshot $(df --output=source " + quoted + " | tail -1 | sed 's|^/dev/||')@sshit-$(date +%s)";
        if (fs.Contains("lvm") || fs.Contains("ext4") || fs.Contains("xfs"))
            return "No filesystem snapshot here. Copy first: cp -a --reflink=auto " + quoted + " " + quoted + ".sshit-bak-$(date +%s)";
        return fs.Length == 0 ? null : "Filesystem " + fileSystem + ": take a copy before running this.";
    }

    private static BlastReport Clone(BlastReport source, string error) => new()
    {
        Command = source.Command, Target = source.Target, Findings = source.Findings,
        Matches = source.Matches, Error = error
    };

    /// <summary>Splits a command line on whitespace, keeping quoted runs together.</summary>
    public static List<string> Tokenise(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var c in command ?? "")
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; current.Append(c); continue; }
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
