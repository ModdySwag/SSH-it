using System.Net;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// Renders one finished command block as a self-contained artifact: an HTML card a human can
/// attach to a ticket, or Markdown for a chat message. No network, no external assets and no
/// telemetry — the file is the proof, and it works on a machine that has never seen SSH-it.
/// </summary>
public static class BlockReceipt
{
    /// <summary>Output is capped so a multi-megabyte command cannot produce an unopenable file.</summary>
    public const int OutputLimit = 200_000;

    public sealed class Receipt
    {
        public string Host { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string? User { get; set; }
        public string? Command { get; set; }
        public string Output { get; set; } = "";
        public string? Cwd { get; set; }
        public int ExitCode { get; set; } = -1;
        public TimeSpan Duration { get; set; }
        public DateTime Started { get; set; }
        public string AppVersion { get; set; } = "";
    }

    public static string AppVersion()
    {
        var version = typeof(BlockReceipt).Assembly.GetName().Version;
        return version is null ? "unknown" : version.ToString();
    }

    /// <summary>"exit 0 · 1.2 s", or "no exit status" when the block never closed.</summary>
    public static string Summarise(Receipt r)
    {
        var exit = r.ExitCode >= 0 ? "exit " + r.ExitCode : "no exit status";
        return exit + " · " + FormatDuration(r.Duration);
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds >= 60 ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
        : d.TotalSeconds >= 1 ? d.TotalSeconds.ToString("0.0") + " s"
        : d.TotalMilliseconds >= 1 ? d.TotalMilliseconds.ToString("0") + " ms"
        : "<1 ms";

    /// <summary>Keeps the head and the tail of a long output, with a marker naming what was cut.</summary>
    public static string Trim(string text, int limit = OutputLimit)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= limit) return text ?? "";
        var head = limit * 2 / 3;
        var tail = limit - head;
        var cut = text.Length - head - tail;
        return text[..head] +
               $"\n[... {cut:N0} characters removed by SSH-it when building this receipt ...]\n" +
               text[^tail..];
    }

    public static string RenderHtml(Receipt r)
    {
        var sb = new StringBuilder();
        var status = r.ExitCode switch
        {
            0 => ("ok", "exit 0"),
            > 0 => ("bad", "exit " + r.ExitCode),
            _ => ("unknown", "no exit status")
        };

        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>SSH-it receipt — {E(r.Host)} — {E(FirstLine(r.Command))}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
            :root { color-scheme: light dark; }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 32px 16px; background: #0f1115; color: #e6e8ec;
                   font: 15px/1.55 "Segoe UI", system-ui, sans-serif; }
            main { max-width: 960px; margin: 0 auto; background: #161a21; border: 1px solid #262c37;
                   border-radius: 14px; overflow: hidden; }
            header { display: flex; align-items: center; gap: 12px; padding: 18px 22px;
                     border-bottom: 1px solid #262c37; }
            header h1 { font-size: 16px; margin: 0; font-weight: 600; letter-spacing: .2px; }
            .pill { margin-left: auto; font: 600 12px/1 "Segoe UI", sans-serif; padding: 6px 10px;
                    border-radius: 999px; border: 1px solid; white-space: nowrap; }
            .pill.ok { color: #56d364; border-color: #2ea04366; background: #2ea04314; }
            .pill.bad { color: #ff7b72; border-color: #f8514966; background: #f8514914; }
            .pill.unknown { color: #9aa4b2; border-color: #30363d; background: #30363d33; }
            dl { display: grid; grid-template-columns: max-content 1fr; gap: 6px 18px;
                 margin: 0; padding: 18px 22px; border-bottom: 1px solid #262c37; }
            dt { color: #8b949e; font-size: 13px; }
            dd { margin: 0; font-size: 13px; overflow-wrap: anywhere; }
            h2 { font: 600 12px/1 "Segoe UI", sans-serif; text-transform: uppercase;
                 letter-spacing: .08em; color: #8b949e; margin: 0; padding: 18px 22px 8px; }
            pre { margin: 0 22px 18px; padding: 14px 16px; background: #0b0e13;
                  border: 1px solid #21262d; border-radius: 8px; overflow: auto;
                  font: 13px/1.5 "Cascadia Mono", Consolas, monospace; white-space: pre-wrap;
                  overflow-wrap: anywhere; }
            pre.cmd { color: #d2a8ff; }
            footer { padding: 14px 22px; border-top: 1px solid #262c37; color: #6e7681; font-size: 12px; }
            @media (prefers-color-scheme: light) {
              body { background: #f6f8fa; color: #1f2328; }
              main { background: #ffffff; border-color: #d0d7de; }
              header, dl, footer { border-color: #d0d7de; }
              pre { background: #f6f8fa; border-color: #d0d7de; }
              pre.cmd { color: #8250df; }
              dt, h2 { color: #656d76; }
              footer { color: #656d76; }
            }
            """);
        sb.AppendLine("</style></head><body><main>");

        sb.AppendLine("<header><h1>SSH-it receipt</h1>");
        sb.AppendLine($"<span class=\"pill {status.Item1}\">{E(status.Item2)}</span></header>");

        sb.AppendLine("<dl>");
        Row(sb, "Host", r.Host);
        if (!string.IsNullOrEmpty(r.User)) Row(sb, "User", r.User);
        Row(sb, "Session", r.SessionName);
        if (!string.IsNullOrEmpty(r.Cwd)) Row(sb, "Directory", r.Cwd);
        Row(sb, "Started", r.Started == default ? "unknown" : r.Started.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Row(sb, "Duration", FormatDuration(r.Duration));
        Row(sb, "Exit status", r.ExitCode >= 0 ? r.ExitCode.ToString() : "not reported by the shell");
        sb.AppendLine("</dl>");

        sb.AppendLine("<h2>Command</h2>");
        sb.AppendLine($"<pre class=\"cmd\">{E(Empty(r.Command, "(the shell sent no command line)"))}</pre>");

        sb.AppendLine("<h2>Output</h2>");
        sb.AppendLine($"<pre>{E(Empty(Trim(r.Output), "(no output)"))}</pre>");

        sb.AppendLine($"<footer>Written locally by SSH-it {E(r.AppVersion)}. " +
                      "No network request was made to produce this file.</footer>");
        sb.AppendLine("</main></body></html>");
        return sb.ToString();
    }

    public static string RenderMarkdown(Receipt r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SSH-it receipt — {r.Host}");
        sb.AppendLine();
        sb.AppendLine($"- **Status:** {Summarise(r)}");
        if (!string.IsNullOrEmpty(r.User)) sb.AppendLine($"- **User:** {r.User}");
        sb.AppendLine($"- **Session:** {r.SessionName}");
        if (!string.IsNullOrEmpty(r.Cwd)) sb.AppendLine($"- **Directory:** `{r.Cwd}`");
        if (r.Started != default) sb.AppendLine($"- **Started:** {r.Started:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- **Client:** SSH-it {r.AppVersion}");
        sb.AppendLine();

        var command = Empty(r.Command, "(the shell sent no command line)");
        var fence = Fence(command + "\n" + r.Output);
        sb.AppendLine("**Command**");
        sb.AppendLine();
        sb.AppendLine(fence + "sh");
        sb.AppendLine(command);
        sb.AppendLine(fence);
        sb.AppendLine();
        sb.AppendLine("**Output**");
        sb.AppendLine();
        sb.AppendLine(fence);
        sb.AppendLine(Empty(Trim(r.Output), "(no output)"));
        sb.AppendLine(fence);
        return sb.ToString();
    }

    /// <summary>A file name a human can find later: time, host, first word of the command.</summary>
    public static string SuggestFileName(Receipt r)
    {
        var stamp = (r.Started == default ? DateTime.Now : r.Started).ToString("yyyyMMdd-HHmmss");
        var host = Slug(r.Host);
        var cmd = Slug(FirstLine(r.Command));
        if (cmd.Length > 40) cmd = cmd[..40].TrimEnd('-', '_');
        var name = $"receipt-{stamp}" + (host.Length > 0 ? "-" + host : "") + (cmd.Length > 0 ? "-" + cmd : "");
        return name + ".html";
    }

    // ---------------------------------------------------------------- helpers

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.AppendLine($"<dt>{E(label)}</dt><dd>{E(value)}</dd>");

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    private static string Empty(string? text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text;

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>A fence longer than any run of backticks inside the text, so output cannot break out.</summary>
    private static string Fence(string text)
    {
        var run = 0;
        var best = 0;
        foreach (var c in text)
        {
            if (c == '`') { run++; if (run > best) best = run; }
            else run = 0;
        }
        return new string('`', Math.Max(3, best + 1));
    }

    private static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
