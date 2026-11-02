namespace SshIt.Services;

/// <summary>
/// Reads a session's ProxyJump chain as data. The chain itself is already handled by OpenSSH
/// (`-J a,b,c`); this only exists so the UI can draw it and say which hop a failure named.
/// </summary>
public static class JumpChain
{
    /// <summary>Splits "a,b , c" into three hops. Blank means no chain at all.</summary>
    public static IReadOnlyList<string> Hops(string? jumpHost)
    {
        if (string.IsNullOrWhiteSpace(jumpHost)) return Array.Empty<string>();

        var hops = new List<string>();
        foreach (var part in jumpHost.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var hop = part.Trim();
            if (hop.Length > 0) hops.Add(hop);
        }
        return hops;
    }

    /// <summary>A hop split into the pieces an ssh command line takes separately.</summary>
    public readonly record struct Hop(string User, string Host, int Port);

    /// <summary>
    /// Splits "ops@jump-a:2222" (or "[2001:db8::1]:22") into user, host and port. A hop has no
    /// default port; 0 means "whatever ssh would use", so the caller leaves -p out.
    /// </summary>
    public static Hop ParseHop(string hop)
    {
        var s = hop.Trim();
        var user = "";
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            user = s[..at];
            s = s[(at + 1)..];
        }

        var port = 0;
        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            var close = s.IndexOf(']');
            if (close > 1)
            {
                var tail = s[(close + 1)..];
                s = s[1..close];
                if (tail.StartsWith(':') && int.TryParse(tail[1..], out var bracketed)) port = bracketed;
            }
        }
        else
        {
            var colon = s.LastIndexOf(':');
            // More than one colon is a bare IPv6 address, not a port.
            if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var plain))
            {
                port = plain;
                s = s[..colon];
            }
        }

        return new Hop(user, s, port);
    }

    /// <summary>
    /// Reads a jump setting that may carry per-hop keys: "a, b=C:\keys\b, c". The hop is everything
    /// before the first '=' (a hop never contains one) and the key everything after it, so a Windows
    /// path with a colon and backslashes survives untouched. A bare hop means "no key of its own".
    /// </summary>
    public static (List<string> Hops, Dictionary<string, string> Keys) ParseSpec(string? spec)
    {
        var hops = new List<string>();
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(spec)) return (hops, keys);

        foreach (var entry in SplitOutsideQuotes(spec.Trim()))
        {
            if (entry.Trim().Length == 0) continue;

            var eq = IndexOutsideQuotes(entry, '=');
            var hop = (eq < 0 ? entry : entry[..eq]).Trim();
            if (hop.Length == 0) continue;
            hops.Add(hop);

            if (eq >= 0)
            {
                var key = entry[(eq + 1)..].Trim();
                // A quoted key may contain the comma that separates hops. Windows paths cannot
                // contain a double quote, so the quotes are delimiters and nothing else.
                if (key.Length >= 2 && key[0] == '"' && key[^1] == '"') key = key[1..^1].Trim();
                if (key.Length > 0) keys[hop] = key;
            }
        }
        return (hops, keys);
    }

    /// <summary>Split on a separator that is not inside double quotes.</summary>
    private static List<string> SplitOutsideQuotes(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static int IndexOutsideQuotes(string text, char wanted)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') quoted = !quoted;
            else if (c == wanted && !quoted) return i;
        }
        return -1;
    }

    /// <summary>The text form the editor and the inventory carry: hops, with "=key" only where a hop has its own.</summary>
    public static string Format(string? jumpHost, IReadOnlyDictionary<string, string>? keys)
    {
        var hops = Hops(jumpHost);
        if (hops.Count == 0) return "";
        var parts = new List<string>(hops.Count);
        foreach (var hop in hops)
        {
            var key = "";
            if (keys is not null)
            {
                foreach (var pair in keys)
                    if (string.Equals(pair.Key.Trim(), hop, StringComparison.OrdinalIgnoreCase)) { key = pair.Value; break; }
            }

            if (string.IsNullOrWhiteSpace(key)) { parts.Add(hop); continue; }

            // Quote a key that carries the hop separator, or that would lose its padding on parse.
            var needsQuotes = key.Contains(',') || !string.Equals(key, key.Trim(), StringComparison.Ordinal);
            parts.Add(hop + "=" + (needsQuotes ? "\"" + key + "\"" : key));
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// The host a hop points at, with user@ and :port removed, for matching against ssh's
    /// error text. Bracketed IPv6 keeps its brackets off.
    /// </summary>
    public static string HostPart(string? hop)
    {
        if (string.IsNullOrWhiteSpace(hop)) return "";

        var s = hop.Trim();
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            var close = s.IndexOf(']');
            if (close > 1) return s[1..close];
        }

        var colon = s.IndexOf(':');
        if (colon > 0) s = s[..colon];
        return s;
    }

    /// <summary>
    /// Which hop ssh blamed, given its stderr. Returns the hop as written in the session,
    /// "target" when the destination itself is named, or null when the text names no host
    /// (in that case the UI says the failure is unattributed rather than guessing).
    /// </summary>
    public static string? FailingHop(string? error, IReadOnlyList<string> hops, string? target)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;

        foreach (var hop in hops)
        {
            var host = HostPart(hop);
            if (host.Length > 0 && error.Contains(host, StringComparison.OrdinalIgnoreCase))
                return hop;
        }

        var targetHost = HostPart(target);
        if (targetHost.Length > 0 && error.Contains(targetHost, StringComparison.OrdinalIgnoreCase))
            return "target";

        return null;
    }

    /// <summary>The first line of stderr that says something, for a one-line status.</summary>
    public static string? FirstLine(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        foreach (var raw in error.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return null;
    }
}
