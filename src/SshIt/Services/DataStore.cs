using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Local-first persistence. Everything lives in one folder the user owns:
/// %APPDATA%\SshIt by default, or .\data next to the executable in portable mode.
/// Writes are atomic (temp file + replace) so a crash can never truncate a session list.
/// </summary>
public sealed class DataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RootDir { get; }
    public bool Portable { get; }

    /// <summary>The data root, resolved once so services that are not the store can find it.</summary>
    public static string RootPath { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var exeDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(exeDir, "SSH-it.portable"))
            ? Path.Combine(exeDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SSH-it");
    }

    /// <summary>Exposed so the regression suite round-trips settings with the real options.</summary>
    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    public AppSettings Settings { get; private set; } = new();
    public List<Session> Sessions { get; private set; } = new();
    public List<SessionFolder> Folders { get; private set; } = new();
    public List<Snippet> Snippets { get; private set; } = new();
    public List<TunnelDef> Tunnels { get; private set; } = new();

    public event Action? SessionsChanged;

    public DataStore()
    {
        Portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "SSH-it.portable"));
        RootDir = RootPath;
        Directory.CreateDirectory(RootDir);
    }

    private string PathFor(string name) => Path.Combine(RootDir, name);

    /// <summary>Files that existed but could not be read; a later save must not overwrite them.</summary>
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        Settings = LoadPart<AppSettings>("settings.json") ?? new AppSettings();
        Sessions = LoadPart<List<Session>>("sessions.json") ?? new List<Session>();
        Folders = LoadPart<List<SessionFolder>>("folders.json") ?? new List<SessionFolder>();
        var snippets = LoadPart<List<Snippet>>("snippets.json", out var snippetsMissing);
        if (snippets is null)
        {
            // Seed ONLY when the file is genuinely absent. `Read` returns null for an unreadable file
            // too, and seeding (then saving) over a locked or permission-denied snippets.json replaced
            // a real list with the five built-ins on the next edit.
            if (snippetsMissing)
            {
                // The seed has to be written on first load, not just returned: fresh ids on every launch
                // make snippet identity unstable, so a settings sync sees five new snippets every time.
                snippets = SeedSnippets();
                Write("snippets.json", snippets);
            }
            else
            {
                snippets = new List<Snippet>();
                Log.Warn("snippets.json is unreadable; the list is empty this session and the file was left alone");
            }
        }
        Snippets = snippets;
        Tunnels = LoadPart<List<TunnelDef>>("tunnels.json") ?? new List<TunnelDef>();

        // Repair dangling references left by a manual edit.
        var folderIds = Folders.Select(f => f.Id).ToHashSet();
        foreach (var s in Sessions.Where(s => s.FolderId is not null && !folderIds.Contains(s.FolderId)))
            s.FolderId = null;

        foreach (var t in Tunnels.Where(t => t.SessionId is not null))
            t.IsActive = false;
    }

    public void SaveSettings() => Write("settings.json", Settings);
    public void SaveSessions() { Write("sessions.json", Sessions); SessionsChanged?.Invoke(); }
    public void SaveFolders() { Write("folders.json", Folders); SessionsChanged?.Invoke(); }
    public void SaveSnippets() => Write("snippets.json", Snippets);
    public void SaveTunnels() => Write("tunnels.json", Tunnels);

    public void SaveAll()
    {
        SaveSettings();
        SaveSessions();
        SaveFolders();
        SaveSnippets();
        SaveTunnels();
    }

    private T? Read<T>(string name) where T : class
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        // A sharing violation is usually a transient AV/indexer lock, not corruption: retry briefly
        // before treating a store as unreadable and falling back to defaults.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "reading " + name);
                TryBackupCorrupt(path);
                return null;
            }
        }
    }

    /// <summary>True when the file is absent (as opposed to present but unreadable).</summary>
    private T? Read<T>(string name, out bool missing) where T : class
    {
        missing = !File.Exists(PathFor(name));
        return Read<T>(name);
    }

    /// <summary>
    /// Reads one store and remembers an unreadable file. Returning defaults for a file that exists
    /// but could not be read is only half the problem: the next save would write the defaults over
    /// it, which is how a locked sessions.json becomes an empty session tree.
    /// </summary>
    private T? LoadPart<T>(string name, out bool missing) where T : class
    {
        var value = Read<T>(name, out missing);
        if (value is null && !missing) _unreadable.Add(name);
        return value;
    }

    private T? LoadPart<T>(string name) where T : class => LoadPart<T>(name, out _);

    private void Write<T>(string name, T value)
    {
        if (_unreadable.Contains(name))
        {
            // Saving would replace a file this session could not read with whatever is in memory.
            Log.Warn("refusing to write " + name + ": it could not be read at startup; the file was left alone");
            return;
        }
        var path = PathFor(name);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            if (File.Exists(path))
            {
                // Replace keeps the original if the move fails.
                var backup = path + ".bak";
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "writing " + name);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            var bad = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture);
            File.Copy(path, bad, true);
        }
        catch { }
    }

    private static List<Snippet> SeedSnippets() => new()
    {
        new Snippet { Name = "Disk usage", Command = "df -h", Description = "Filesystem usage for all mounts" },
        new Snippet { Name = "Top processes", Command = "ps aux --sort=-%cpu | head -n 15", Description = "CPU hogs" },
        new Snippet { Name = "Listening ports", Command = "ss -tulpn", Description = "Sockets and owning process" },
        new Snippet { Name = "Tail syslog", Command = "tail -f /var/log/syslog", Description = "Follow the system log" },
        new Snippet { Name = "Uptime", Command = "uptime", Description = "Load average and uptime" },
    };
}
