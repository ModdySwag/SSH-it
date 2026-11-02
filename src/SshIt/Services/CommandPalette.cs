using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One row in the palette: what it says, and what to run when it is chosen.</summary>
public sealed class PaletteItem
{
    public string Kind { get; init; } = "action";   // session | snippet | history | service | tunnel | snapshot | action | setting
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Payload { get; init; } = "";      // an id, a command line, or a restore command
    public string Keywords { get; init; } = "";     // extra text that may match but is not shown

    public string Describe() => Kind + ": " + Title;
}

/// <summary>One menu command the palette can run. The id is the contract with MainWindow's switch.</summary>
public sealed record PaletteAction(string Id, string Title, string Group, string Gesture, string Keywords);

/// <summary>
/// R3-23: the command palette's model. Everything here is pure — building rows from the stores and
/// ranking them against a query — so the ranking, the scope prefix and the providers are unit-tested
/// without a window. The window (Views/PaletteWindow.cs) only draws the rows and reports the choice.
///
/// Matching is a per-token subsequence match with bonuses for an exact title, a prefix and a word
/// start, so "snip" finds "Snippets" ahead of a snippet called "Inspect nginx", and typing a host
/// finds the session that connects to it. Every token must match somewhere: extra words narrow the
/// list rather than widening it. A leading '>' scopes the query to commands and settings, which is
/// how the palette stays usable when there are hundreds of history rows.
/// </summary>
public static class CommandPalette
{
    public const int MaxResults = 200;

    /// <summary>Kinds a leading '&gt;' keeps.</summary>
    private static bool IsCommandKind(string kind) => kind is "action" or "setting";

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Every action the palette offers. Ids are stable: the palette passes one to MainWindow, which
    /// switches on it, so an id that drifts silently becomes a no-op row.
    /// </summary>
    public static readonly PaletteAction[] Actions =
    {
        new("new-session",     "New session…",                  "File",  "Ctrl+N",       "host connect add"),
        new("quick-connect",   "Quick connect…",                "File",  "Ctrl+K",       "host address jump"),
        new("local-terminal",  "New local terminal",            "File",  "Ctrl+Shift+L", "powershell shell local"),
        new("new-window",      "New window",                    "File",  "Ctrl+Shift+N", "second window"),
        new("import-ssh",      "Import from OpenSSH config…",   "File",  "",             "ssh_config hosts"),
        new("import-putty",    "Import from PuTTY…",            "File",  "",             "registry sessions"),
        new("export-sessions", "Export sessions…",              "File",  "",             "backup json"),
        new("import-sessions", "Import sessions…",              "File",  "",             "restore json"),
        new("save-workspace",  "Save workspace…",               "File",  "",             "layout tabs window"),
        new("open-data",       "Open data folder",              "File",  "",             "appdata settings files"),
        new("exit",            "Exit SSH-it",                   "File",  "Alt+F4",       "quit close"),

        new("copy",            "Copy",                          "Edit",  "Ctrl+Shift+C", "clipboard selection"),
        new("paste",           "Paste",                         "Edit",  "Ctrl+Shift+V", "clipboard"),
        new("select-all",      "Select all",                    "Edit",  "Ctrl+Shift+A", "text"),
        new("find",            "Find in the terminal…",         "Edit",  "Ctrl+F",       "search scrollback"),
        new("clear-scrollback","Clear scrollback",              "Edit",  "",             "wipe buffer history"),

        new("zoom-in",         "Zoom in",                       "View",  "Ctrl+=",       "font bigger"),
        new("zoom-out",        "Zoom out",                      "View",  "Ctrl+-",       "font smaller"),
        new("zoom-reset",      "Reset zoom",                    "View",  "Ctrl+0",       "font default"),
        new("reset-opacity",   "Reset window opacity",          "View",  "",             "transparent fade"),
        new("fullscreen",      "Full screen",                   "View",  "F11",          "maximise maximize"),
        new("split-right",     "Split right",                   "View",  "Ctrl+Shift+D", "pane vertical"),
        new("split-down",      "Split down",                    "View",  "Ctrl+Shift+E", "pane horizontal"),
        new("close-pane",      "Close pane",                    "View",  "Ctrl+Shift+Q", "split"),
        new("send-to-all",     "Send to every pane…",           "View",  "Ctrl+Shift+Enter", "fanout broadcast every tab"),
        new("focus-mode",      "Focus mode",                     "View",  "Ctrl+Shift+Z", "hide panels zen terminal"),

        new("connect",         "Connect",                       "Session", "Enter",      "open"),
        new("disconnect",      "Disconnect",                    "Session", "",           "close connection"),
        new("reconnect",       "Reconnect",                     "Session", "",           "retry"),
        new("diagnose",        "Diagnose connection…",          "Session", "Ctrl+Shift+G", "why handshake ssh -G"),
        new("posture",         "Fleet posture…",                "Session", "",           "algorithms security audit"),
        new("scan",            "Network scan…",                 "Session", "",           "discover subnet cidr range hosts lan"),
        new("watch",           "Watch a command…",              "Session", "",           "poll repeat diff"),
        new("diff",            "Compare command output…",       "Session", "",           "two hosts diff"),
        new("blast",           "Blast radius…",                 "Session", "",           "destructive preview glob"),
        new("approve",         "Approve a command for agents…",  "Session", "",           "lease agent token"),
        new("new-tab",         "New tab",                       "Session", "Ctrl+Shift+T", "duplicate session"),
        new("close-tab",       "Close tab",                     "Session", "Ctrl+Shift+W", ""),
        new("close-all-tabs",  "Close all tabs",                "Session", "",           ""),
        new("tear-off",        "Move tab to a new window",      "Session", "Ctrl+Shift+M", "detach"),
        new("send-zmodem",     "Send file over ZMODEM…",        "Session", "",           "transfer upload"),
        new("edit-session",    "Edit session…",                 "Session", "F2",          "properties"),
        new("duplicate-session","Duplicate session",            "Session", "",           "copy"),
        new("delete-session",  "Delete session",                "Session", "Del",        "remove"),

        new("files",           "Files (SFTP)",                  "Tools", "Ctrl+Shift+F", "transfer download upload"),
        new("tunnels",         "Port forwarding",               "Tools", "",             "tunnel forward socks"),
        new("snippets",        "Snippets",                      "Tools", "",             "commands saved"),
        new("lock",            "Lock session…",                 "Tools", "Ctrl+Shift+K", "password protect"),
        new("info",            "Host information",              "Tools", "Ctrl+Shift+S", "load cpu memory chart"),
        new("performance",     "Performance",                   "Tools", "",             "frames echo latency bench measure"),
        new("key-manager",     "Key manager…",                  "Tools", "",             "ssh keys generate"),
        new("shell-integration","Shell integration script…",    "Tools", "",             "osc 133 prompt marks"),
        new("play-recording",  "Play recording…",               "Tools", "",             "cast replay"),
        new("options",         "Options…",                      "Tools", "Ctrl+,",       "settings preferences"),

        new("shortcuts",       "Keyboard shortcuts",            "Help",  "",             "keys gestures"),
        new("about",           "About SSH-it",                  "Help",  "",             "version")
    };

    public static List<PaletteItem> ActionItems()
    {
        var items = new List<PaletteItem>(Actions.Length);
        foreach (var action in Actions)
        {
            items.Add(new PaletteItem
            {
                Kind = "action",
                Title = action.Title,
                Subtitle = "Action · " + action.Group + (action.Gesture.Length > 0 ? " · " + action.Gesture : ""),
                Payload = action.Id,
                Keywords = action.Keywords
            });
        }
        return items;
    }

    // ---------------------------------------------------------------- providers

    public static List<PaletteItem> SessionItems(IEnumerable<Session> sessions, int cap = 40)
    {
        var items = new List<PaletteItem>();
        foreach (var session in sessions)
        {
            if (items.Count >= cap) break;
            var title = string.IsNullOrWhiteSpace(session.Name) ? session.DisplayTarget : session.Name;
            items.Add(new PaletteItem
            {
                Kind = "session",
                Title = title,
                Subtitle = "Session · " + session.DisplayTarget + (session.IsLocal ? " · local" : ""),
                Payload = session.Id,
                Keywords = session.Host + " " + session.User + " " + session.Kind + " " + (session.Notes ?? "")
            });
        }
        return items;
    }

    public static List<PaletteItem> SnippetItems(IEnumerable<Snippet> snippets, int cap = 40)
    {
        var items = new List<PaletteItem>();
        foreach (var snippet in snippets)
        {
            if (items.Count >= cap) break;
            var folder = string.IsNullOrWhiteSpace(snippet.Folder) ? "" : snippet.Folder + " · ";
            items.Add(new PaletteItem
            {
                Kind = "snippet",
                Title = snippet.Name,
                Subtitle = "Snippet · " + folder + OneLine(snippet.Command),
                Payload = snippet.Id,
                Keywords = snippet.Command + " " + snippet.Description
            });
        }
        return items;
    }

    /// <summary>
    /// The command ledger, newest first. Rows come from the signed evidence chain (kind "block"), so
    /// the palette recalls what was actually run on which host — not a text file that can drift.
    /// Repeats collapse into one row with a count, which is what makes a 400-row tail useful.
    /// </summary>
    public static List<PaletteItem> HistoryItems(IEnumerable<JsonObject> rows, int cap = 60)
    {
        var list = rows as IList<JsonObject> ?? rows.ToList();
        var order = new List<string>();
        var newest = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var scanned = 0;
        for (var i = list.Count - 1; i >= 0 && scanned < 4000; i--)
        {
            scanned++;
            var row = list[i];
            if (Text(row, "kind") != "block") continue;
            if (row["data"] is not JsonObject data) continue;
            var command = Text(data, "command").Trim();
            if (command.Length == 0) continue;

            if (counts.TryGetValue(command, out var seen))
            {
                counts[command] = seen + 1;
                continue;
            }
            if (order.Count >= cap) continue;   // still count repeats of rows already listed
            counts[command] = 1;
            order.Add(command);
            newest[command] = row;
        }

        var items = new List<PaletteItem>(order.Count);
        foreach (var command in order)
        {
            var row = newest[command];
            var data = row["data"] as JsonObject;
            var host = Text(row, "host");
            var exit = data?["exit"]?.GetValue<int>() ?? 0;
            var millis = data?["duration_ms"]?.GetValue<int>() ?? 0;
            var cwd = Text(data, "cwd");
            var at = DateTime.TryParse(Text(row, "at"), out var stamp)
                ? stamp.ToLocalTime().ToString("MM-dd HH:mm")
                : "";

            var subtitle = "History · ";
            if (host.Length > 0) subtitle += host + " · ";
            subtitle += "exit " + exit + " · " + (millis / 1000.0).ToString("0.0") + " s";
            if (at.Length > 0) subtitle += " · " + at;
            if (counts[command] > 1) subtitle += " · " + counts[command] + "×";

            items.Add(new PaletteItem
            {
                Kind = "history",
                Title = OneLine(command),
                Subtitle = subtitle,
                Payload = command,
                Keywords = cwd
            });
        }
        return items;
    }

    /// <summary>
    /// C9: the command ledger, newest first. The output is part of the search text, so a word that
    /// only ever appeared in a command's output still finds the command that produced it.
    /// </summary>
    /// <summary>Saved layouts, one row each; choosing one reopens its tabs.</summary>
    public static List<PaletteItem> WorkspaceItems(IEnumerable<Workspace> workspaces, int cap = 30)
    {
        var items = new List<PaletteItem>();
        foreach (var workspace in workspaces)
        {
            if (items.Count >= cap) break;
            items.Add(new PaletteItem
            {
                Kind = "workspace",
                Title = workspace.Name,
                Subtitle = "Workspace · " + workspace.Tabs.Count + " tab(s) · " + workspace.Tool + " panel",
                Payload = workspace.Name,
                Keywords = "layout window tabs"
            });
        }
        return items;
    }

    public static List<PaletteItem> LedgerItems(IReadOnlyList<LedgerRow> rows, int cap = 60)
    {
        var items = new List<PaletteItem>();
        for (var i = rows.Count - 1; i >= 0 && items.Count < cap; i--)
        {
            var row = rows[i];
            var where = string.IsNullOrWhiteSpace(row.Session) ? row.Host : row.Session;
            var snippet = OneLine(row.Output);
            if (snippet.Length > 160) snippet = snippet[..160];
            items.Add(new PaletteItem
            {
                Kind = "history",
                Title = OneLine(row.Command),
                Subtitle = "Ledger · " + where + " · exit " + row.Exit + " · "
                           + (row.DurationMs / 1000.0).ToString("0.0") + " s · " + row.When,
                Payload = row.Command,
                Keywords = row.Cwd + " " + snippet
            });
        }
        return items;
    }

    public static List<PaletteItem> ServiceItems(IEnumerable<ServiceDefinition> services, int cap = 40)
    {
        var items = new List<PaletteItem>();
        foreach (var service in services)
        {
            if (items.Count >= cap) break;
            var note = string.IsNullOrWhiteSpace(service.Note) ? "" : " · " + service.Note;
            items.Add(new PaletteItem
            {
                Kind = "service",
                Title = service.Name,
                Subtitle = "Service · " + service.Spec + note,
                Payload = service.Name,
                Keywords = service.Dsn + " " + service.CheckCommand
            });
        }
        return items;
    }

    public static List<PaletteItem> TunnelItems(IEnumerable<TunnelSupervisor.Entry> tunnels, int cap = 20)
    {
        var items = new List<PaletteItem>();
        foreach (var tunnel in tunnels)
        {
            if (items.Count >= cap) break;
            items.Add(new PaletteItem
            {
                Kind = "tunnel",
                Title = tunnel.Forward,
                Subtitle = "Tunnel · " + (tunnel.SessionName.Length > 0 ? tunnel.SessionName : tunnel.SessionId)
                           + " · pid " + tunnel.Pid + " · open the panel to manage it",
                Payload = tunnel.Id,
                Keywords = tunnel.Spec
            });
        }
        return items;
    }

    public static List<PaletteItem> SnapshotItems(IEnumerable<UndoPoint> points, int cap = 30)
    {
        var items = new List<PaletteItem>();
        foreach (var point in points)
        {
            if (items.Count >= cap) break;
            items.Add(new PaletteItem
            {
                Kind = "snapshot",
                Title = point.Target,
                Subtitle = "Undo point · " + point.Kind + " · " + point.Path + " · choose to copy the restore command",
                Payload = point.Restore,
                Keywords = point.Snapshot
            });
        }
        return items;
    }

    /// <summary>
    /// Toggles, with their current value in the title so the palette can answer "is it on?" without
    /// opening Options. Broadcast only appears when the active tab actually has two panes: it is a
    /// per-tab flag, and offering it on a single pane would toggle something with no visible effect.
    /// </summary>
    public static List<PaletteItem> SettingItems(bool statusBar, bool sidebar, bool rightPanel, bool pin,
                                                 bool broadcast, bool hasSplit)
    {
        var items = new List<PaletteItem>
        {
            Setting("Status bar — " + OnOff(statusBar), "set:statusbar", "View · bottom bar", "status footer"),
            Setting("Session sidebar — " + OnOff(sidebar), "set:sidebar", "View · hosts tree", "folder list"),
            Setting("Tools panel — " + OnOff(rightPanel), "set:rightpanel", "View · files, tunnels, info", "dock right"),
            Setting("Pin to desktop — " + OnOff(pin), "set:pin", "Window · stays behind other windows", "always on bottom"),
        };
        if (hasSplit)
            items.Add(Setting("Broadcast input — " + OnOff(broadcast), "set:broadcast",
                "This tab · types into every pane", "mirror multi pane"));
        return items;
    }

    /// <summary>
    /// One row per theme the app knows, straight from the catalog, with the current one ticked. A
    /// cycling "next theme" row would make the user press it and look; naming them is shorter.
    /// </summary>
    public static List<PaletteItem> ThemeItems(IEnumerable<(string Id, string Label)> entries, string current)
    {
        var items = new List<PaletteItem>();
        foreach (var (id, label) in entries)
        {
            var isCurrent = string.Equals(id, current, StringComparison.OrdinalIgnoreCase);
            items.Add(new PaletteItem
            {
                Kind = "setting",
                Title = (isCurrent ? "✓ " : "") + label,
                Subtitle = "Theme · appearance" + (isCurrent ? " · current" : ""),
                Payload = "theme:" + id,
                Keywords = "theme colour color palette " + label
            });
        }
        return items;
    }

    private static PaletteItem Setting(string title, string payload, string subtitle, string keywords) => new()
    {
        Kind = "setting",
        Title = title,
        Subtitle = "Setting · " + subtitle,
        Payload = payload,
        Keywords = keywords
    };

    private static string OnOff(bool value) => value ? "on" : "off";

    // ---------------------------------------------------------------- ranking

    /// <summary>
    /// Filters and ranks. A score below zero means the row does not match; ties keep provider order,
    /// so the list does not reshuffle between two keystrokes that match equally well.
    /// </summary>
    public static List<PaletteItem> Rank(IEnumerable<PaletteItem> items, string? query, int limit = MaxResults)
    {
        var scored = new List<(PaletteItem Item, int Score, int Index)>();
        var index = 0;
        foreach (var item in items)
        {
            var score = Match(query ?? "", item);
            if (score >= 0) scored.Add((item, score, index));
            index++;
        }

        return scored
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Index)
            .Take(Math.Max(0, limit))
            .Select(row => row.Item)
            .ToList();
    }

    /// <summary>Score for one row against the whole query; -1 means it is filtered out.</summary>
    public static int Match(string? query, PaletteItem item)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return 0;

        var commandsOnly = text[0] == '>';
        if (commandsOnly)
        {
            text = text[1..].Trim();
            if (!IsCommandKind(item.Kind)) return -1;
            if (text.Length == 0) return 0;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 0;

        var total = 0;
        foreach (var token in tokens)
        {
            var best = Best(token, item.Title);
            var subtitle = Best(token, item.Subtitle);
            if (subtitle >= 0) best = Math.Max(best, subtitle - 12);
            var keywords = Best(token, item.Keywords);
            if (keywords >= 0) best = Math.Max(best, keywords - 18);
            if (best < 0) return -1;
            total += best;
        }

        // A short title is a better answer than a long one that merely contains the same letters.
        total += Math.Max(0, 30 - item.Title.Length / 2);
        return total;
    }

    /// <summary>Best score for one token inside one field; -1 when the token is not there at all.</summary>
    public static int Best(string token, string? text)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(text)) return -1;

        var query = token.ToLowerInvariant();
        var haystack = text.ToLowerInvariant();

        if (haystack == query) return 260;

        var at = haystack.IndexOf(query, StringComparison.Ordinal);
        if (at == 0) return 200 + Math.Min(40, query.Length * 2);
        if (at > 0 && IsBoundary(text[at - 1])) return 170 + Math.Min(30, query.Length * 2);

        // Subsequence: every character in order, gaps allowed but penalised, runs rewarded.
        var score = 40;
        var from = 0;
        var last = -2;
        var first = -1;
        foreach (var c in query)
        {
            var found = haystack.IndexOf(c, from);
            if (found < 0) return -1;
            if (first < 0) first = found;
            if (found == last + 1) score += 6;
            else if (last >= 0) score -= Math.Min(6, found - last - 2);
            last = found;
            from = found + 1;
        }
        if (first == 0) score += 10;
        else if (first > 0 && IsBoundary(text[first - 1])) score += 8;

        return Math.Max(1, score);
    }

    private static bool IsBoundary(char c) => c is ' ' or '-' or '_' or '.' or '/' or '\\' or ':' or '(' or '[' or ',';

    private static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>The chip drawn at the left of every row.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "session" => "Session",
        "snippet" => "Snippet",
        "history" => "History",
        "service" => "Service",
        "tunnel" => "Tunnel",
        "snapshot" => "Undo",
        "setting" => "Setting",
        _ => "Action"
    };

    private static string Text(JsonObject? row, string key)
    {
        if (row is null) return "";
        try
        {
            return row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        }
        catch
        {
            return "";
        }
    }
}
