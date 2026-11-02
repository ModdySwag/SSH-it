using System.IO;
using System.Text;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One host in an inventory file.</summary>
public sealed class InventoryHost
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string Key { get; set; } = "";
    public string Jump { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool AllowAgent { get; set; }

    /// <summary>`forwardagent: true` - -A, so the target can use the local agent.</summary>
    public bool ForwardAgent { get; set; }
    public string Kind { get; set; } = "Ssh";
    public string Note { get; set; } = "";
    public bool Local { get; set; }

    /// <summary>tmux session name for Kind == "Tmux"; blank attaches to the most recent one.</summary>
    public string TmuxSession { get; set; } = "";
}

public sealed class InventoryDocument
{
    public int Version { get; set; } = 1;
    public List<InventoryHost> Hosts { get; } = new();
}

public sealed class InventoryIssue
{
    public string Severity { get; set; } = "error";   // error | warning
    public int Line { get; set; }
    public string Message { get; set; } = "";
    public override string ToString() => (Line > 0 ? "line " + Line + ": " : "") + Message;
}

public sealed class InventoryPlan
{
    public string Action { get; set; } = "same";      // add | update | same
    public string Name { get; set; } = "";
    public List<string> Changes { get; } = new();
}

/// <summary>
/// R3-21: a host inventory a team can keep in git. The file is a deliberately small YAML subset —
/// a flat `hosts:` list of mappings — so it can be read without a YAML package and validated in CI.
///
/// Honest limits: groups, anchors, multi-line strings and nested mappings are NOT supported and are
/// refused with a line number rather than half-read; `validate` is offline (it checks shape and
/// consistency, not reachability), and an import only adds/updates sessions by name — it never deletes
/// one, so removing a host is a deliberate act in the GUI.
/// </summary>
public static class Inventory
{
    /// <summary>
    /// Reads an inventory file as STRICT UTF-8. File.ReadAllText replaces invalid bytes with U+FFFD,
    /// so a name mangled by an editor imported "ok" and could never be typed again.
    /// </summary>
    public static string ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            // GetString does not strip the preamble the way File.ReadAllText did; a BOM'd file would
            // otherwise fail with "\uFEFFversion outside a host item".
            return decoded.Length > 0 && decoded[0] == '\uFEFF' ? decoded[1..] : decoded;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "the file is not valid UTF-8 (first bad byte at offset " + ex.Index + ") — re-save it as UTF-8");
        }
    }

    private static readonly string[] KnownKeys =
    {
        "name", "host", "port", "user", "key", "jump", "tags", "allowagent", "agent", "forwardagent",
        "forward", "kind", "note",
        "local", "tmux", "version", "hosts"
    };

    private static readonly string[] Kinds = { "Ssh", "Telnet", "Serial", "Local", "Tmux" };

    // ---------------------------------------------------------------- parsing

    public static InventoryDocument Parse(string text, List<InventoryIssue> issues)
    {
        var document = new InventoryDocument();
        InventoryHost? current = null;
        var inHosts = false;
        var sawHosts = false;
        HashSet<string>? seenKeys = null;   // per host item, so a repeated key is reported
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var lineNumber = i + 1;
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith("#")) continue;
            if (raw.Contains('\t'))
            {
                issues.Add(new InventoryIssue { Line = lineNumber, Message = "tabs are not allowed in this subset — use spaces" });
                continue;
            }

            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();

            if (line.StartsWith("- "))
            {
                if (!inHosts)
                {
                    issues.Add(new InventoryIssue { Line = lineNumber, Message = "a list item outside `hosts:` — this subset only supports a flat hosts list" });
                    continue;
                }
                current = new InventoryHost();
                document.Hosts.Add(current);
                seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rest = line.Substring(2).Trim();
                if (rest.Length > 0) ApplyKey(current, rest, lineNumber, issues, seenKeys);
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                issues.Add(new InventoryIssue { Line = lineNumber, Message = "not `key: value` — " + Shorten(line) });
                continue;
            }

            var key = line.Substring(0, colon).Trim().ToLowerInvariant();
            var value = line.Substring(colon + 1).Trim();

            if (indent == 0 && key == "version")
            {
                if (!TryCleanScalar(value, out var versionText, out var versionError)
                    || !int.TryParse(versionText, out var version) || version != 1)
                    issues.Add(new InventoryIssue
                    {
                        Line = lineNumber,
                        Message = "version must be 1 (got '" + Shorten(versionError ?? value) + "')"
                    });
                else document.Version = version;
                continue;
            }

            if (indent == 0 && key == "hosts")
            {
                if (value.Length > 0)
                {
                    issues.Add(new InventoryIssue { Line = lineNumber, Message = "`hosts:` must start a list on the following lines" });
                    continue;
                }
                inHosts = true;
                sawHosts = true;
                current = null;
                continue;
            }

            if (indent == 0 && key == "groups")
            {
                issues.Add(new InventoryIssue
                {
                    Line = lineNumber,
                    Message = "`groups:` is not supported by this subset — flatten the hosts into one `hosts:` list (tags can carry the group)"
                });
                continue;
            }

            if (!inHosts || current is null)
            {
                issues.Add(new InventoryIssue { Line = lineNumber, Message = "`" + key + "` outside a host item" });
                continue;
            }

            if (indent == 0)
            {
                issues.Add(new InventoryIssue { Line = lineNumber, Message = "`" + key + "` must be indented under a `- ` item" });
                continue;
            }

            ApplyKey(current, line, lineNumber, issues, seenKeys);
        }

        if (!sawHosts)
            issues.Add(new InventoryIssue { Message = "no `hosts:` list — the file defines no hosts" });

        return document;
    }

    private static void ApplyKey(InventoryHost host, string line, int lineNumber, List<InventoryIssue> issues,
                                 HashSet<string>? seenKeys = null)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            issues.Add(new InventoryIssue { Line = lineNumber, Message = "not `key: value` — " + Shorten(line) });
            return;
        }

        var key = line.Substring(0, colon).Trim().ToLowerInvariant();
        var value = line.Substring(colon + 1).Trim();
        if (!KnownKeys.Contains(key))
        {
            issues.Add(new InventoryIssue { Line = lineNumber, Message = "unknown key `" + key + "`" });
            return;
        }

        // The parser assigns as it reads, so a key that appears twice in one host silently keeps
        // the last value. Say it out loud: an inventory file is often generated, and a duplicate
        // is a mistake in the generator, not a preference.
        if (seenKeys is not null && !seenKeys.Add(key))
            issues.Add(new InventoryIssue { Line = lineNumber, Message = "duplicate key `" + key + "` in one host — the last value would win without a word" });

        // Refuse what this subset cannot represent instead of reading half of it: an inline list is
        // not a scalar, an unterminated quote is not a value, and a trailing comment is not text.
        if (key != "tags" && !TryCleanScalar(value, out value, out var scalarError))
        {
            issues.Add(new InventoryIssue { Line = lineNumber, Message = "`" + Shorten(key) + "`: " + scalarError });
            return;
        }

        switch (key)
        {
            case "name": host.Name = value; break;
            case "host": host.Host = value; break;
            case "user": host.User = value; break;
            case "key": host.Key = value; break;
            case "jump": host.Jump = value; break;
            case "kind": host.Kind = value; break;
            case "tmux": host.TmuxSession = value; break;
            case "note": host.Note = value; break;
            case "tags":
                if (!TryList(value, out var tags, out var tagError))
                    issues.Add(new InventoryIssue { Line = lineNumber, Message = "tags: " + tagError });
                else host.Tags = tags;
                break;
            case "port":
                if (int.TryParse(value, out var port)) host.Port = port;
                else issues.Add(new InventoryIssue { Line = lineNumber, Message = "port is not a number: '" + Shorten(value) + "'" });
                break;
            case "allowagent":
            case "agent":
                host.AllowAgent = IsTrue(value);
                break;
            case "forwardagent":
            case "forward":
                host.ForwardAgent = IsTrue(value);
                break;
            case "local":
                host.Local = IsTrue(value);
                break;
        }
    }

    /// <summary>
    /// A scalar this subset understands. Refuses an inline collection, an unterminated quote, and
    /// text after a closing quote; strips a trailing `# comment` only when the `#` follows whitespace
    /// (YAML's own rule, so `a#b` stays a value).
    /// </summary>
    private static bool TryCleanScalar(string input, out string value, out string? error)
    {
        value = input.Trim();
        error = null;
        if (value.Length == 0) return true;

        if (value[0] is '[' or '{')
        {
            error = "inline collections are not supported here — use one value per line, or `tags:` for a list";
            return false;
        }

        if (value[0] is '"' or '\'')
        {
            var quote = value[0];
            var end = -1;
            for (var i = 1; i < value.Length; i++)
            {
                if (quote == '"' && value[i] == '\\') { i++; continue; }
                if (value[i] == quote) { end = i; break; }
            }
            if (end < 0)
            {
                error = "unterminated quote";
                return false;
            }
            var after = value[(end + 1)..].Trim();
            if (after.Length > 0 && !after.StartsWith('#'))
            {
                error = "unexpected text after a quoted value: " + Shorten(after);
                return false;
            }
            value = value[1..end].Replace("\\\"", "\"");
            return true;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '#') continue;
            if (i > 0 && !char.IsWhiteSpace(value[i - 1])) continue;
            value = value[..i].TrimEnd();
            break;
        }
        return true;
    }

    private static bool TryList(string input, out List<string> values, out string? error)
    {
        values = new List<string>();
        error = null;
        var text = input.Trim();
        if (text.StartsWith('[') && text.EndsWith(']')) text = text[1..^1];
        else if (text.StartsWith('[') || text.EndsWith(']'))
        {
            error = "unterminated inline list";
            return false;
        }

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryCleanScalar(raw, out var item, out error)) return false;
            if (item.Length > 0) values.Add(item);
        }
        return true;
    }

    private static bool IsTrue(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "yes" || value == "1";

    private static string Shorten(string text) => text.Length <= 60 ? text : text.Substring(0, 60) + "…";

    // ---------------------------------------------------------------- validation

    /// <summary>Shape and consistency checks. Offline: nothing here connects to a host.</summary>
    public static List<InventoryIssue> Validate(InventoryDocument document, List<InventoryIssue> issues)
    {
        var result = issues;
        var byName = new Dictionary<string, InventoryHost>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in document.Hosts)
        {
            if (string.IsNullOrWhiteSpace(host.Name))
            {
                result.Add(new InventoryIssue { Severity = "error", Message = "a host has no name" });
                continue;
            }
            if (!byName.TryAdd(host.Name, host))
                result.Add(new InventoryIssue { Severity = "error", Message = "duplicate host name '" + host.Name + "'" });

            if (!host.Local && string.IsNullOrWhiteSpace(host.Host))
                result.Add(new InventoryIssue { Severity = "error", Message = "'" + host.Name + "' has no host" });
            if (host.Port < 1 || host.Port > 65535)
                result.Add(new InventoryIssue { Severity = "error", Message = "'" + host.Name + "' has port " + host.Port + " (1..65535)" });
            if (!Kinds.Contains(host.Kind, StringComparer.OrdinalIgnoreCase))
                result.Add(new InventoryIssue { Severity = "error", Message = "'" + host.Name + "' has kind '" + host.Kind + "' (one of " + string.Join(", ", Kinds) + ")" });
            if (host.Name.Length > 64)
                result.Add(new InventoryIssue { Severity = "warning", Message = "'" + host.Name + "' has a very long name" });
        }

        foreach (var host in document.Hosts.Where(h => !string.IsNullOrWhiteSpace(h.Jump)))
        {
            foreach (var hop in host.Jump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(hop, host.Name, StringComparison.OrdinalIgnoreCase))
                    result.Add(new InventoryIssue { Severity = "error", Message = "'" + host.Name + "' jumps through itself" });
                else if (!byName.ContainsKey(hop))
                    result.Add(new InventoryIssue { Severity = "warning", Message = "'" + host.Name + "' jumps through '" + hop + "', which is not in this inventory" });
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in document.Hosts.Where(h => !string.IsNullOrWhiteSpace(h.Host)))
            if (!seen.Add(host.User + "@" + host.Host + ":" + host.Port))
                result.Add(new InventoryIssue { Severity = "warning", Message = "another host already points at " + host.User + "@" + host.Host + ":" + host.Port });

        return result;
    }

    public static bool HasErrors(List<InventoryIssue> issues) => issues.Any(i => i.Severity == "error");

    // ---------------------------------------------------------------- export and import

    /// <summary>The inventory form of the saved sessions, in a stable order, so it can be reviewed in git.</summary>
    public static string ToYaml(IEnumerable<Session> sessions)
    {
        var text = new StringBuilder();
        text.AppendLine("# SSH-it host inventory (R3-21). Validate in CI: SSH-it.exe --inventory-validate <file>");
        text.AppendLine("version: 1");
        text.AppendLine("hosts:");
        foreach (var session in sessions.Where(s => !s.IsLocal).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine("  - name: " + Quote(session.Name));
            text.AppendLine("    host: " + Quote(session.Host));
            if (session.Port != 22) text.AppendLine("    port: " + session.Port);
            if (!string.IsNullOrWhiteSpace(session.User)) text.AppendLine("    user: " + Quote(session.User));
            if (!string.IsNullOrWhiteSpace(session.KeyPath)) text.AppendLine("    key: " + Quote(session.KeyPath!));
            if (!string.IsNullOrWhiteSpace(session.JumpHost))
                text.AppendLine("    jump: " + Quote(JumpChain.Format(session.JumpHost, session.JumpKeys)));
            if (session.Tags.Count > 0) text.AppendLine("    tags: [" + string.Join(", ", session.Tags.Select(Quote)) + "]");
            if (session.AllowAgent) text.AppendLine("    allowAgent: true");
            if (session.ForwardAgent) text.AppendLine("    forwardagent: true");
            if (!string.Equals(session.Kind, "Ssh", StringComparison.OrdinalIgnoreCase)) text.AppendLine("    kind: " + Quote(session.Kind));
            if (!string.IsNullOrWhiteSpace(session.TmuxSession)) text.AppendLine("    tmux: " + Quote(session.TmuxSession!));
            if (!string.IsNullOrWhiteSpace(session.Notes)) text.AppendLine("    note: " + Quote(session.Notes!));
        }

        return text.ToString();
    }

    private static string Quote(string value)
        => value.Length == 0 || value.Any(c => ": #[]{}".Contains(c)) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    /// <summary>What an import would do: add new names, update changed ones, leave the rest.</summary>
    public static List<InventoryPlan> Plan(IEnumerable<Session> sessions, InventoryDocument document)
    {
        var existing = sessions.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var plans = new List<InventoryPlan>();
        foreach (var host in document.Hosts)
        {
            if (!existing.TryGetValue(host.Name, out var session))
            {
                plans.Add(new InventoryPlan { Action = "add", Name = host.Name });
                continue;
            }

            var changes = new List<string>();
            if (!string.Equals(session.Host, host.Host, StringComparison.Ordinal)) changes.Add("host " + session.Host + " → " + host.Host);
            if (session.Port != host.Port) changes.Add("port " + session.Port + " → " + host.Port);
            if (!string.Equals(session.User, host.User, StringComparison.Ordinal)) changes.Add("user " + session.User + " → " + host.User);
            if (!string.Equals(session.KeyPath ?? "", host.Key, StringComparison.Ordinal)) changes.Add("key");
            if (!string.Equals(session.JumpHost ?? "", host.Jump, StringComparison.Ordinal)) changes.Add("jump");
            if (!session.Tags.SequenceEqual(host.Tags, StringComparer.OrdinalIgnoreCase)) changes.Add("tags");
            if (session.AllowAgent != host.AllowAgent) changes.Add("allowAgent");
            if (session.ForwardAgent != host.ForwardAgent) changes.Add("forward agent");
            if (!string.Equals(session.Notes ?? "", host.Note, StringComparison.Ordinal)) changes.Add("note");
            if (!string.Equals(session.TmuxSession ?? "", host.TmuxSession, StringComparison.Ordinal)) changes.Add("tmux session");

            var plan = new InventoryPlan { Action = changes.Count == 0 ? "same" : "update", Name = host.Name };
            plan.Changes.AddRange(changes);
            plans.Add(plan);
        }

        return plans;
    }

    /// <summary>Applies a plan to the session list. Never removes a session.</summary>
    public static void Apply(List<Session> sessions, InventoryDocument document)
    {
        var byName = sessions.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var host in document.Hosts)
        {
            // The inventory's jump line may carry "=key" per hop, exactly as the editor shows it.
            var (jumpHops, jumpKeys) = JumpChain.ParseSpec(host.Jump);
            var jumpHost = jumpHops.Count == 0 ? null : string.Join(",", jumpHops);
            var jumpKeyMap = jumpKeys.Count == 0 ? null : jumpKeys;

            if (byName.TryGetValue(host.Name, out var session))
            {
                session.Host = host.Host;
                session.Port = host.Port;
                session.User = host.User;
                session.KeyPath = host.Key.Length > 0 ? host.Key : session.KeyPath;
                session.JumpHost = jumpHost;
                session.JumpKeys = jumpKeyMap;
                session.Tags = host.Tags.Count > 0 ? host.Tags : session.Tags;
                session.AllowAgent = host.AllowAgent;
                session.ForwardAgent = host.ForwardAgent;
                if (host.Note.Length > 0) session.Notes = host.Note;
                if (host.TmuxSession.Length > 0) session.TmuxSession = host.TmuxSession;
                if (!string.Equals(session.Kind, host.Kind, StringComparison.OrdinalIgnoreCase)) session.Kind = host.Kind;
                continue;
            }

            sessions.Add(new Session
            {
                Name = host.Name,
                Host = host.Host,
                Port = host.Port,
                User = host.User,
                KeyPath = host.Key.Length > 0 ? host.Key : null,
                JumpHost = jumpHost,
                JumpKeys = jumpKeyMap,
                Tags = host.Tags,
                AllowAgent = host.AllowAgent,
                ForwardAgent = host.ForwardAgent,
                Notes = host.Note.Length > 0 ? host.Note : null,
                Kind = host.Kind,
                TmuxSession = host.TmuxSession.Length > 0 ? host.TmuxSession : null
            });
        }
    }

    // ---------------------------------------------------------------- reporting

    public static string IssueText(List<InventoryIssue> issues, int hostCount)
    {
        var errors = issues.Count(i => i.Severity == "error");
        var warnings = issues.Count - errors;
        if (issues.Count == 0) return "inventory ok — " + hostCount + " host(s), no errors or warnings";

        var lines = new List<string> { hostCount + " host(s), " + errors + " error(s), " + warnings + " warning(s)" };
        lines.AddRange(issues.Select(i => (i.Severity == "error" ? "  ERROR   " : "  warning ") + i));
        return string.Join("\n", lines);
    }

    public static string PlanText(List<InventoryPlan> plans)
    {
        var added = plans.Count(p => p.Action == "add");
        var updated = plans.Count(p => p.Action == "update");
        var same = plans.Count(p => p.Action == "same");
        var lines = new List<string> { added + " to add, " + updated + " to update, " + same + " unchanged" };
        foreach (var plan in plans.Where(p => p.Action != "same"))
            lines.Add("  " + plan.Action.PadRight(7) + plan.Name
                      + (plan.Changes.Count > 0 ? "  (" + string.Join(", ", plan.Changes) + ")" : ""));
        return string.Join("\n", lines);
    }
}
