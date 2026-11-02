using System.Globalization;
using System.Text;

namespace SshIt.Services;

/// <summary>One line of tmux control mode, already decoded.</summary>
public enum TmuxEventKind
{
    Begin, End, Error, Output, LayoutChange, SessionChanged,
    WindowAdd, WindowClose, WindowRenamed, Exit, Pause, Continue, Unknown
}

public sealed class TmuxEvent
{
    public TmuxEventKind Kind { get; init; }
    public int CommandId { get; init; } = -1;
    public int PaneId { get; init; } = -1;
    public string WindowId { get; init; } = "";
    public string SessionId { get; init; } = "";
    /// <summary>Decoded payload: output text, a command's collected response, a name, or a layout.</summary>
    public string Text { get; init; } = "";
}

/// <summary>
/// tmux control mode (`tmux -CC`) is a line protocol on a raw stream: `%begin/%end/%error` bracket a
/// command's response, `%output %N` carries pane bytes with non-printables escaped as \ooo, and
/// `%layout-change` describes the window as a nested size tree. It must never go through ConPTY or the
/// VT parser: `\033P1000p` is eaten and escapes are mangled on the way.
///
/// This type is the protocol half only — parser, layout tree, and the commands the client sends. The
/// transport (ssh -tt on a raw pipe) and the pane UI come next.
/// </summary>
public sealed class TmuxControlParser
{
    private readonly Dictionary<int, List<string>> _pending = new();
    private int _open = -1;

    /// <summary>Feeds one line (with or without its trailing CR) and returns the event it produced.</summary>
    public TmuxEvent? FeedLine(string line)
    {
        if (line is null) return null;
        line = line.TrimEnd('\r');

        var space = line.IndexOf(' ');
        var verb = line.StartsWith("%", StringComparison.Ordinal)
            ? (space < 0 ? line[1..] : line[1..space])
            : "";
        var rest = space < 0 ? "" : line[(space + 1)..];

        // A response line can ITSELF begin with '%' (list-panes -F '#{pane_id}' answers "%3"), so the
        // rule is not "starts with %". Inside %begin..%end only the bracket and the known
        // notifications are protocol; everything else — echo, output, a pane id — is response data.
        var protocol = verb is "begin" or "end" or "error" or "output" or "extended-output"
            or "layout-change" or "session-changed" or "window-add" or "window-close"
            or "window-renamed" or "exit" or "pause" or "continue";
        if (!protocol)
        {
            if (_open >= 0)
            {
                if (_pending.TryGetValue(_open, out var openBucket)) openBucket.Add(line);
                return null;
            }
            if (verb.Length == 0) return null;
            return new TmuxEvent { Kind = TmuxEventKind.Unknown, Text = line };
        }

        switch (verb)
        {
            case "begin":
            {
                var id = CommandId(rest);
                if (id >= 0) _pending[id] = new List<string>();
                _open = id;
                return new TmuxEvent { Kind = TmuxEventKind.Begin, CommandId = id };
            }

            case "end":
            case "error":
            {
                var id = CommandId(rest);
                List<string>? lines = null;
                if (id >= 0 && _pending.TryGetValue(id, out var bucket)) { lines = bucket; _pending.Remove(id); }
                _open = -1;
                return new TmuxEvent
                {
                    Kind = verb == "end" ? TmuxEventKind.End : TmuxEventKind.Error,
                    CommandId = id,
                    Text = lines is null ? "" : string.Join("\n", lines)
                };
            }

            case "output":
            {
                var split = rest.IndexOf(' ');
                var paneToken = split < 0 ? rest : rest[..split];
                var data = split < 0 ? "" : rest[(split + 1)..];
                var pane = paneToken.StartsWith('%') && int.TryParse(paneToken[1..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
                return new TmuxEvent { Kind = TmuxEventKind.Output, PaneId = pane, Text = Unescape(data) };
            }

            case "extended-output":
            {
                // `%extended-output %N <age> : <text>` — same bytes, with an age field before the colon.
                var split = rest.IndexOf(' ');
                var paneToken = split < 0 ? rest : rest[..split];
                var pane = paneToken.StartsWith('%') && int.TryParse(paneToken[1..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
                var colon = rest.IndexOf(" : ", StringComparison.Ordinal);
                var data = colon < 0 ? "" : rest[(colon + 3)..];
                return new TmuxEvent { Kind = TmuxEventKind.Output, PaneId = pane, Text = Unescape(data) };
            }

            case "layout-change":
            {
                var parts = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                return new TmuxEvent
                {
                    Kind = TmuxEventKind.LayoutChange,
                    WindowId = parts.Length > 0 ? parts[0] : "",
                    Text = parts.Length > 1 ? parts[1] : ""
                };
            }

            case "session-changed":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                return new TmuxEvent
                {
                    Kind = TmuxEventKind.SessionChanged,
                    SessionId = parts.Length > 0 ? parts[0] : "",
                    Text = parts.Length > 1 ? parts[1] : ""
                };
            }

            case "window-add":
            case "window-close":
                return new TmuxEvent
                {
                    Kind = verb == "window-add" ? TmuxEventKind.WindowAdd : TmuxEventKind.WindowClose,
                    WindowId = rest.Trim()
                };

            case "window-renamed":
            {
                var split = rest.IndexOf(' ');
                return new TmuxEvent
                {
                    Kind = TmuxEventKind.WindowRenamed,
                    WindowId = split < 0 ? rest : rest[..split],
                    Text = split < 0 ? "" : rest[(split + 1)..]
                };
            }

            case "exit":
                return new TmuxEvent { Kind = TmuxEventKind.Exit, Text = rest.Trim() };

            case "pause":
            case "continue":
            {
                var token = rest.Split(' ', 2)[0];
                var pane = token.StartsWith('%') && int.TryParse(token[1..], out var p) ? p : -1;
                return new TmuxEvent
                {
                    Kind = verb == "pause" ? TmuxEventKind.Pause : TmuxEventKind.Continue, PaneId = pane
                };
            }

            default:
                return new TmuxEvent { Kind = TmuxEventKind.Unknown, Text = line };
        }
    }

    private static int CommandId(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id : -1;
    }

    /// <summary>`\015` is CR and `\\` is a backslash; everything else is passed through.</summary>
    public static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { builder.Append(text[i]); continue; }
            if (i + 3 < text.Length
                && text[i + 1] is >= '0' and <= '7'
                && text[i + 2] is >= '0' and <= '7'
                && text[i + 3] is >= '0' and <= '7')
            {
                builder.Append((char)Convert.ToInt32(text.Substring(i + 1, 3), 8));
                i += 3;
            }
            else if (i + 1 < text.Length) builder.Append(text[++i]);
        }
        return builder.ToString();
    }

    // ---------------------------------------------------------------- commands the client sends

    /// <summary>`send-keys -H` takes bytes, so a UTF-8 character cannot be split by the shell.</summary>
    public static string SendKeysHex(int pane, string text)
        => "send-keys -t %" + pane + " -H "
           + string.Join(' ', Encoding.UTF8.GetBytes(text).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    /// <summary>tmux sizes the client, not the other way round; a resize is a command to the server.</summary>
    public static string ResizeClient(int width, int height)
        => "refresh-client -C " + Math.Max(1, width) + "x" + Math.Max(1, height);

    /// <summary>Re-sync after %pause: the pane's visible text, with escapes, as one command's response.</summary>
    public static string CapturePane(int pane) => "capture-pane -p -e -t %" + pane;

    public const string Detach = "detach-client";
}

/// <summary>One node of a tmux layout: a container has children, a pane has a pane id.</summary>
public sealed class TmuxLayout
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>-1 for a container; otherwise the tmux pane id.</summary>
    public int PaneId { get; set; } = -1;
    public List<TmuxLayout> Children { get; } = new();
    public bool IsPane => PaneId >= 0;

    public IEnumerable<TmuxLayout> Leaves()
    {
        if (IsPane) yield return this;
        else foreach (var child in Children) foreach (var leaf in child.Leaves()) yield return leaf;
    }

    /// <summary>
    /// Parses `bb62,80x24,0,0{40x24,0,0,0,39x24,41,0,1}`. The leading token is the window (or layout
    /// checksum); each cell is `WxH,X,Y` followed by either `,PANEID` or a braced child list.
    /// </summary>
    public static TmuxLayout Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("empty layout");
        var comma = text.IndexOf(',');
        if (comma < 0) throw new FormatException("layout has no checksum: " + text);
        var name = text[..comma];
        var (root, rest) = ParseCell(text[(comma + 1)..]);
        root.Name = name;
        if (rest.Length > 0 && rest != ",") throw new FormatException("trailing layout text: " + rest);
        return root;
    }

    private static (TmuxLayout Cell, string Remaining) ParseCell(string text)
    {
        var cell = new TmuxLayout();
        var at = 0;
        var width = ReadInt(text, ref at); Expect(text, ref at, 'x');
        var height = ReadInt(text, ref at); Expect(text, ref at, ',');
        var x = ReadInt(text, ref at); Expect(text, ref at, ',');
        var y = ReadInt(text, ref at);
        cell.Width = width; cell.Height = height; cell.X = x; cell.Y = y;

        // tmux spells a left/right split with braces and a top/bottom split with brackets:
        //   80x24{40x24,0,0,0,39x24,41,0,1}      two panes side by side
        //   80x24[80x12,0,0,6,80x11,0,13,7]      two panes stacked
        // Reading only braces made every vertical split a FormatException, so a split-down layout
        // never reached the grid at all.
        if (at < text.Length && text[at] is '{' or '[')
        {
            var close = text[at] == '{' ? '}' : ']';
            at++;
            while (true)
            {
                var (child, restAt) = ParseCellAt(text, at);
                cell.Children.Add(child);
                at = restAt;
                if (at < text.Length && text[at] == ',') { at++; continue; }
                if (at < text.Length && text[at] == close) { at++; break; }
                throw new FormatException("unterminated layout container");
            }
        }
        else if (at < text.Length && text[at] == ',')
        {
            at++;
            cell.PaneId = ReadInt(text, ref at);
        }
        return (cell, text[at..]);
    }

    private static (TmuxLayout Cell, int At) ParseCellAt(string text, int at) => ParseCell(text[at..]) switch
    {
        var (cell, rest) => (cell, text.Length - rest.Length)
    };

    private static int ReadInt(string text, ref int at)
    {
        var start = at;
        while (at < text.Length && text[at] is >= '0' and <= '9') at++;
        if (at == start) throw new FormatException("expected a number at " + start + " in layout");
        return int.Parse(text[start..at], CultureInfo.InvariantCulture);
    }

    private static void Expect(string text, ref int at, char want)
    {
        if (at >= text.Length || text[at] != want)
            throw new FormatException("expected '" + want + "' at " + at + " in layout");
        at++;
    }
}
