using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

public sealed class KnownHostEntry
{
    public string HostField { get; init; } = "";
    public string KeyType { get; init; } = "";
    public string Blob { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public int Line { get; init; }
}

public sealed class HostKeyVerdict
{
    public string KeyType { get; init; } = "";
    public string Stored { get; init; } = "";
    public string Current { get; init; } = "";
    public string Status { get; init; } = "";   // match | changed | new | missing
}

public sealed class HostKeyReport
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string KnownHostsPath { get; init; } = "";
    public List<KnownHostEntry> Stored { get; init; } = new();
    public List<KnownHostEntry> Current { get; init; } = new();
    public List<HostKeyVerdict> Verdicts { get; init; } = new();
    public List<string> SharedHosts { get; init; } = new();
    public string? Note { get; init; }
}

/// <summary>
/// Host-key forensics: what known_hosts holds for a host, what the server offers now, whether they
/// agree, and which other hosts already trust the same key. The last part is the useful one on a
/// mismatch: one host with a new key after a reinstall looks the same as a man in the middle, but
/// forty hosts suddenly presenting one key does not.
/// </summary>
public static class HostKeys
{
    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ssh", "known_hosts");
    }

    /// <summary>OpenSSH's own "SHA256:base64" fingerprint of a base64 key blob.</summary>
    public static string Fingerprint(string base64Blob)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64Blob); }
        catch { return ""; }
        var hash = SHA256.HashData(bytes);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>True when a known_hosts host field covers this host and port.</summary>
    public static bool MatchesHost(string hostField, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(hostField)) return false;
        var candidates = port == 22
            ? new[] { host }
            : new[] { host, $"[{host}]:{port}" };

        foreach (var raw in hostField.Split(','))
        {
            var field = raw.Trim();
            if (field.Length == 0) continue;
            if (field.StartsWith("|1|", StringComparison.Ordinal))
            {
                foreach (var candidate in candidates)
                    if (HashedMatches(field, candidate)) return true;
                continue;
            }
            foreach (var candidate in candidates)
                if (string.Equals(field, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HashedMatches(string field, string name)
    {
        var parts = field.Split('|');
        if (parts.Length != 4 || parts[1] != "1") return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            using var hmac = new HMACSHA1(salt);
            var actual = hmac.ComputeHash(Encoding.UTF8.GetBytes(name));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public static List<KnownHostEntry> Stored(string host, int port, string? path = null)
    {
        var entries = new List<KnownHostEntry>();
        path ??= DefaultPath();
        if (!File.Exists(path)) return entries;

        var lineNumber = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            lineNumber++;
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (!MatchesHost(fields[0], host, port)) continue;
            entries.Add(new KnownHostEntry
            {
                HostField = fields[0],
                KeyType = fields[1],
                Blob = fields[2],
                Fingerprint = Fingerprint(fields[2]),
                Line = lineNumber
            });
        }
        return entries;
    }

    /// <summary>Keys the server offers right now, via a bounded ssh-keyscan.</summary>
    public static List<KnownHostEntry> Scan(string host, int port, int timeoutSeconds = 6)
    {
        var entries = new List<KnownHostEntry>();
        var args = port == 22 ? $"-T {timeoutSeconds} {host}" : $"-p {port} -T {timeoutSeconds} {host}";
        try
        {
            var info = new ProcessStartInfo("ssh-keyscan", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null) return entries;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            foreach (var line in output.Split('\n'))
            {
                var text = line.TrimEnd('\r');
                if (text.Length == 0 || text.StartsWith('#')) continue;
                var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3) continue;
                entries.Add(new KnownHostEntry
                {
                    HostField = fields[0],
                    KeyType = fields[1],
                    Blob = fields[2],
                    Fingerprint = Fingerprint(fields[2])
                });
            }
        }
        catch
        {
            // ssh-keyscan absent or refused: an empty list is an honest answer.
        }
        return entries;
    }

    /// <summary>Host names in known_hosts that trust exactly this key blob.</summary>
    public static List<string> SharedHosts(string blob, string excludeHost, string? path = null)
    {
        var names = new List<string>();
        path ??= DefaultPath();
        if (!File.Exists(path) || string.IsNullOrEmpty(blob)) return names;

        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[2] != blob) continue;
            foreach (var field in fields[0].Split(','))
            {
                var name = field.Trim();
                if (name.Length == 0) continue;
                if (name.StartsWith("|1|", StringComparison.Ordinal)) continue;   // cannot be reversed
                if (name.Contains(excludeHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(name)) names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// Host fields of @cert-authority lines that cover this host. When one matches, the host's key
    /// is authenticated by a CA rather than by a pinned blob, which is why a rotation does not
    /// show up as "changed".
    /// </summary>
    public static List<string> CertAuthorities(string host, int port, string? path = null)
    {
        var names = new List<string>();
        path ??= DefaultPath();
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(host)) return names;

        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (!text.StartsWith("@cert-authority", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = text["@cert-authority".Length..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (!HostPatternMatches(fields[0], host, port)) continue;
            if (!names.Contains(fields[0])) names.Add(fields[0]);
        }
        return names;
    }

    /// <summary>
    /// known_hosts host fields may carry * and ? wildcards. Exact entries and hashed entries are
    /// handled by <see cref="MatchesHost"/>; this adds the glob form that @cert-authority lines
    /// almost always use. Deliberately kept out of MatchesHost so Forget() stays exact-only and
    /// cannot delete a wildcard line by accident.
    /// </summary>
    public static bool HostPatternMatches(string hostField, string host, int port)
    {
        if (MatchesHost(hostField, host, port)) return true;
        if (string.IsNullOrWhiteSpace(hostField)) return false;

        var candidates = port == 22 ? new[] { host } : new[] { host, $"[{host}]:{port}" };
        foreach (var raw in hostField.Split(','))
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern.StartsWith("|1|", StringComparison.Ordinal)) continue;
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0) continue;
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            foreach (var candidate in candidates)
                if (Regex.IsMatch(candidate, regex, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    public static HostKeyReport Inspect(Session session, string? path = null)
    {
        path ??= DefaultPath();
        var host = session.Host;
        var port = session.Port;

        var stored = Stored(host, port, path);
        var current = Scan(host, port);
        var verdicts = new List<HostKeyVerdict>();

        foreach (var keyType in stored.Select(s => s.KeyType).Concat(current.Select(c => c.KeyType)).Distinct())
        {
            var before = stored.FirstOrDefault(s => s.KeyType == keyType);
            var now = current.FirstOrDefault(c => c.KeyType == keyType);
            var status = before is null ? "new" : now is null ? "missing"
                : before.Blob == now.Blob ? "match" : "changed";
            verdicts.Add(new HostKeyVerdict
            {
                KeyType = keyType,
                Stored = before?.Fingerprint ?? "",
                Current = now?.Fingerprint ?? "",
                Status = status
            });
        }

        var changed = verdicts.FirstOrDefault(v => v.Status == "changed");
        var shared = changed is null
            ? new List<string>()
            : SharedHosts(stored.First(s => s.KeyType == changed.KeyType).Blob, host, path);

        return new HostKeyReport
        {
            Host = host,
            Port = port,
            KnownHostsPath = path,
            Stored = stored,
            Current = current,
            Verdicts = verdicts,
            SharedHosts = shared,
            Note = current.Count == 0
                ? "ssh-keyscan returned nothing: the host is unreachable, or its key cannot be read from here."
                : null
        };
    }

    /// <summary>
    /// Removes this host's entries, leaving every other line byte-identical. Writes a timestamped
    /// .bak first; a refused write leaves the original untouched.
    /// </summary>
    public static bool Forget(string host, int port, out int removed, out string? backup, string? path = null)
    {
        removed = 0;
        backup = null;
        path ??= DefaultPath();
        if (!File.Exists(path)) return false;

        var text = File.ReadAllText(path);
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var kept = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { kept.Add(line); continue; }
            var fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3 && MatchesHost(fields[0], host, port)) { removed++; continue; }
            kept.Add(line);
        }
        if (removed == 0) return false;

        backup = path + ".sshit-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(path, backup, overwrite: false);
        File.WriteAllText(path, string.Join(eol, kept));
        return true;
    }
}
