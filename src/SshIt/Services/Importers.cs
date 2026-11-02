using System.IO;
using System.Text.RegularExpressions;
using SshIt.Models;
using Microsoft.Win32;

namespace SshIt.Services;

/// <summary>Reads ~/.ssh/config (including Include) into session templates.</summary>
public static class SshConfigParser
{
    public sealed class Entry
    {
        public string Alias = "";
        public string? HostName;
        public string? User;
        public int Port = 22;
        public string? IdentityFile;
        public string? ProxyJump;
        public List<string> Patterns = new();
    }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public static List<Entry> Parse()
    {
        var entries = new List<Entry>();
        var path = ConfigPath;
        if (!File.Exists(path)) return entries;
        ParseFile(path, entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        return entries;
    }

    private static void ParseFile(string path, List<Entry> entries, HashSet<string> visited, int depth)
    {
        if (depth > 6 || !visited.Add(path) || !File.Exists(path)) return;

        Entry? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var space = line.IndexOfAny(new[] { ' ', '\t', '=' });
            string key, value;
            if (space < 0) { key = line; value = ""; }
            else
            {
                key = line[..space];
                value = line[(space + 1)..].TrimStart('=', ' ', '\t');
            }

            switch (key.ToLowerInvariant())
            {
                case "host":
                    current = new Entry();
                    current.Patterns.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    if (current.Patterns.Count > 0) current.Alias = current.Patterns[0];
                    entries.Add(current);
                    break;
                case "match":
                    current = null; // Match blocks are contextual; skip rather than guess.
                    break;
                case "include":
                    var pattern = value.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    if (!Path.IsPathRooted(pattern))
                        pattern = Path.Combine(Path.GetDirectoryName(path)!, pattern);
                    foreach (var file in ExpandGlob(pattern)) ParseFile(file, entries, visited, depth + 1);
                    break;
                default:
                    if (current is null) break;
                    switch (key.ToLowerInvariant())
                    {
                        case "hostname": current.HostName = value; break;
                        case "user": current.User = value; break;
                        case "port": if (int.TryParse(value, out var p)) current.Port = p; break;
                        case "identityfile":
                            current.IdentityFile = value.Replace("~",
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                            break;
                        case "proxyjump": current.ProxyJump = value; break;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<string> ExpandGlob(string pattern)
    {
        var dir = Path.GetDirectoryName(pattern);
        var file = Path.GetFileName(pattern);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(dir, file).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static List<Session> ToSessions()
    {
        var list = new List<Session>();
        foreach (var entry in Parse())
        {
            if (entry.Patterns.Any(p => p.Contains('*') || p.Contains('?'))) continue;
            if (entry.Alias is "*") continue;

            list.Add(new Session
            {
                Name = entry.Alias,
                Host = entry.HostName ?? entry.Alias,
                User = entry.User ?? "",
                Port = entry.Port,
                KeyPath = entry.IdentityFile,
                JumpHost = entry.ProxyJump,
                AuthMode = entry.IdentityFile is null ? "Agent" : "Key",
                UseConfigAlias = false,
                Notes = "Imported from ~/.ssh/config"
            });
        }
        return list;
    }
}

/// <summary>Reads saved PuTTY sessions out of the registry.</summary>
public static class PuttyImporter
{
    public static List<Session> Import()
    {
        var list = new List<Session>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
            if (root is null) return list;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;

                string? Get(string value) => key.GetValue(value)?.ToString();

                var host = Get("HostName");
                if (string.IsNullOrWhiteSpace(host)) host = Uri.UnescapeDataString(name);

                var session = new Session
                {
                    Name = Uri.UnescapeDataString(name),
                    Host = host!,
                    User = Get("UserName") ?? "",
                    Port = int.TryParse(Get("PortNumber"), out var p) ? p : 22,
                    Notes = "Imported from PuTTY"
                };

                var keyFile = Get("PublicKeyFile");
                if (!string.IsNullOrWhiteSpace(keyFile))
                {
                    session.KeyPath = keyFile;
                    session.AuthMode = "Key";
                }

                list.Add(session);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("PuTTY import: " + ex.Message);
        }
        return list;
    }
}
