using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>What one collection's merge did.</summary>
public sealed class CollectionReport
{
    public int Added { get; set; }
    public int Identical { get; set; }
    public int Conflicting { get; set; }
    public int Replaced { get; set; }
    public List<string> Conflicts { get; } = new();

    public string Describe() =>
        Added + " added, " + Replaced + " replaced, " + Conflicting + " conflicting, " + Identical + " unchanged";
}

/// <summary>Everything one export/import/status did, per collection.</summary>
public sealed class SyncReport
{
    public string Folder { get; init; } = "";
    public CollectionReport Sessions { get; set; } = new();
    public CollectionReport Folders { get; set; } = new();
    public CollectionReport Snippets { get; set; } = new();
    public CollectionReport Workspaces { get; set; } = new();
    public int SettingsApplied { get; set; }
    public bool DryRun { get; set; }

    /// <summary>Files in the sync folder that existed but could not be read; empty is not "0 rows".</summary>
    public List<string> ReadErrors { get; } = new();

    /// <summary>Set when the import refused before writing anything (nothing was changed).</summary>
    public string? Error { get; set; }

    public string Describe()
    {
        var lines = new List<string>
        {
            (DryRun ? "Would import from " : "Imported from ") + Folder + ":",
            "  sessions   " + Sessions.Describe(),
            "  folders    " + Folders.Describe(),
            "  snippets   " + Snippets.Describe(),
            "  workspaces " + Workspaces.Describe()
        };
        if (SettingsApplied > 0) lines.Add("  settings   " + SettingsApplied + " preference(s) applied");
        if (Error is { Length: > 0 }) lines.Add("  refused    " + Error);
        foreach (var readError in ReadErrors) lines.Add("  unreadable " + readError);
        foreach (var conflict in Sessions.Conflicts.Concat(Folders.Conflicts).Concat(Snippets.Conflicts))
            lines.Add("  conflict   " + conflict + " — kept this machine's copy (use --overwrite to take the folder's)");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        folder = Folder,
        dryRun = DryRun,
        settingsApplied = SettingsApplied,
        sessions = new { added = Sessions.Added, replaced = Sessions.Replaced, conflicting = Sessions.Conflicting, unchanged = Sessions.Identical, conflicts = Sessions.Conflicts },
        folders = new { added = Folders.Added, replaced = Folders.Replaced, conflicting = Folders.Conflicting, unchanged = Folders.Identical, conflicts = Folders.Conflicts },
        snippets = new { added = Snippets.Added, replaced = Snippets.Replaced, conflicting = Snippets.Conflicting, unchanged = Snippets.Identical, conflicts = Snippets.Conflicts },
        workspaces = new { added = Workspaces.Added, replaced = Workspaces.Replaced, conflicting = 0, unchanged = Workspaces.Identical },
        readErrors = ReadErrors
    });
}

/// <summary>
/// Settings sync through the user's own git: export the portable state into a folder, let git move it,
/// import it on the other machine. The rules are deliberately conservative:
///
///   - Sessions, folders and snippets merge by id. A missing item is added; an item that differs is a
///     CONFLICT and this machine keeps its copy unless --overwrite says otherwise. Importing never deletes.
///   - Workspaces carry an Updated stamp, so the newer copy wins without asking.
///   - Preferences are machine-local by default (window placement, paths, open tabs). The portable ones
///     are applied only with --with-settings, and machine-local keys are stripped on export.
///   - Secrets are never in these files: passwords live in Credential Manager, leases and the signed
///     policy are device-bound and are not exported at all.
/// </summary>
public static class SettingsSync
{
    public const int Version = 1;

    /// <summary>Keys inside settings.json that belong to one machine and must not travel.</summary>
    public static readonly string[] MachineLocalSettings =
    {
        "WindowLeft", "WindowTop", "WindowWidth", "WindowHeight", "WindowMaximized", "PinToDesktop",
        "LastSelectedSessionId", "OpenTabSessionIds", "RecentSessionIds", "SftpLocalPath",
        "ZmodemDownloadFolder", "SidebarWidth", "RightPanelWidth", "WindowOpacity", "AgentGate",
        "ExtraWindows"
    };

    private static readonly string[] VolatileSessionKeys = { "LastUsed", "UseCount" };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Window placement defaults to NaN, which JSON cannot carry without saying so — the same
        // reason DataStore's own options set this.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    // ---------------------------------------------------------------- fingerprints and merging

    /// <summary>Canonical text for a session, ignoring the fields that change on every use.</summary>
    public static string SessionFingerprint(Session session) => Fingerprint(session, VolatileSessionKeys);

    public static string Fingerprint<T>(T item, params string[] ignoreKeys)
    {
        var node = JsonSerializer.SerializeToNode(item, Options) as JsonObject ?? new JsonObject();
        foreach (var key in ignoreKeys) node.Remove(key);
        return node.ToJsonString();
    }

    /// <summary>
    /// Merges one collection by id: add what is missing, count what differs, replace only when asked.
    /// Pure apart from the list it is handed, so the rules are testable without a store.
    /// </summary>
    public static CollectionReport Merge<T>(List<T> local, IEnumerable<T> remote,
                                            Func<T, string> key, Func<T, string> fingerprint,
                                            bool overwrite) where T : class
    {
        var report = new CollectionReport();

        // Index the local collection once. The scan-per-item version was O(n*m): a 120k-row ledger
        // took 125 s before it gave up. Key equality stays ordinal, as the previous == comparison was.
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < local.Count; i++) index[key(local[i])] = i;

        foreach (var item in remote)
        {
            var id = key(item);
            if (!index.TryGetValue(id, out var position))
            {
                index[id] = local.Count;
                local.Add(item);
                report.Added++;
                continue;
            }

            var existing = local[position];
            if (fingerprint(existing) == fingerprint(item))
            {
                report.Identical++;
                continue;
            }

            if (overwrite)
            {
                local[position] = item;
                report.Replaced++;
            }
            else
            {
                report.Conflicting++;
                report.Conflicts.Add(id);
            }
        }
        return report;
    }

    /// <summary>Workspaces are named layouts with a stamp: the newer copy wins without a conflict.</summary>
    public static CollectionReport MergeWorkspaces(List<Workspace> local, IEnumerable<Workspace> remote,
                                                   ICollection<Workspace>? changed = null)
    {
        var report = new CollectionReport();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < local.Count; i++) index[local[i].Name] = i;

        foreach (var workspace in remote)
        {
            if (!index.TryGetValue(workspace.Name, out var position))
            {
                index[workspace.Name] = local.Count;
                local.Add(workspace);
                changed?.Add(workspace);
                report.Added++;
                continue;
            }

            var existing = local[position];
            if (Fingerprint(existing) == Fingerprint(workspace))
            {
                report.Identical++;
                continue;
            }

            var mine = DateTime.TryParse(existing.Updated, out var a) ? a : DateTime.MinValue;
            var theirs = DateTime.TryParse(workspace.Updated, out var b) ? b : DateTime.MinValue;
            if (theirs > mine)
            {
                local[position] = workspace;
                changed?.Add(workspace);
                report.Replaced++;
            }
            else
            {
                report.Identical++;
            }
        }
        return report;
    }

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Writes the portable state into <paramref name="folder"/>. Machine-local settings are stripped;
    /// everything is written beside a manifest that says what produced it.
    /// </summary>
    public static string Export(DataStore store, string folder, string appVersion)
    {
        Directory.CreateDirectory(folder);

        WriteJson(Path.Combine(folder, "sessions.json"), store.Sessions);
        WriteJson(Path.Combine(folder, "folders.json"), store.Folders);
        WriteJson(Path.Combine(folder, "snippets.json"), store.Snippets);

        var workspaces = WorkspaceStore.Load();
        WriteJson(Path.Combine(folder, "workspaces.json"), workspaces);

        WriteJson(Path.Combine(folder, "settings.json"), PortableSettings(store.Settings));

        var manifest = new JsonObject
        {
            ["version"] = Version,
            ["app"] = appVersion,
            ["exportedUtc"] = DateTime.UtcNow.ToString("O"),
            ["device"] = Environment.MachineName,
            ["counts"] = new JsonObject
            {
                ["sessions"] = store.Sessions.Count,
                ["folders"] = store.Folders.Count,
                ["snippets"] = store.Snippets.Count,
                ["workspaces"] = workspaces.Count
            },
            ["carries"] = "sessions, folders, snippets, workspaces, portable settings",
            ["never"] = "passwords (Credential Manager), leases, the signed policy, the evidence chain"
        };
        WriteJson(Path.Combine(folder, "manifest.json"), manifest);

        return folder;
    }

    /// <summary>The settings that travel: everything except the keys that describe this machine.</summary>
    public static JsonObject PortableSettings(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, Options) as JsonObject ?? new JsonObject();
        foreach (var key in MachineLocalSettings) node.Remove(key);
        return node;
    }

    private static void WriteJson(string path, object? value)
    {
        // Write beside the target and move it into place: a half-written settings file in a synced
        // folder is a corrupt import waiting to happen on the other machine.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    // ---------------------------------------------------------------- import

    public static SyncReport Import(DataStore store, string folder, bool overwrite, bool withSettings, bool dryRun)
    {
        var report = new SyncReport { Folder = folder, DryRun = dryRun };

        var remoteSessions = ReadList<Session>(Path.Combine(folder, "sessions.json"), report.ReadErrors);
        var remoteFolders = ReadList<SessionFolder>(Path.Combine(folder, "folders.json"), report.ReadErrors);
        var remoteSnippets = ReadList<Snippet>(Path.Combine(folder, "snippets.json"), report.ReadErrors);
        var remoteWorkspaces = ReadList<Workspace>(Path.Combine(folder, "workspaces.json"), report.ReadErrors);

        if (!dryRun && withSettings)
        {
            var settingsPath = Path.Combine(folder, "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    JsonNode.Parse(File.ReadAllText(settingsPath));
                }
                catch (Exception ex)
                {
                    // Refuse before touching the store: a half-applied import is worse than none.
                    report.Error = "settings.json is not valid JSON: " + ex.Message + " — nothing was imported";
                    return report;
                }
            }
        }

        if (!dryRun)
        {
            report.Sessions = Merge(store.Sessions, remoteSessions, s => s.Id, SessionFingerprint, overwrite);
            report.Folders = Merge(store.Folders, remoteFolders, f => f.Id, f => Fingerprint(f), overwrite);
            report.Snippets = Merge(store.Snippets, remoteSnippets, s => s.Id, s => Fingerprint(s), overwrite);
            var changedWorkspaces = new List<Workspace>();
            report.Workspaces = MergeWorkspaces(WorkspaceStore.Load().ToList(), remoteWorkspaces, changedWorkspaces);

            if (report.Sessions.Added + report.Sessions.Replaced > 0) store.SaveSessions();
            if (report.Folders.Added + report.Folders.Replaced > 0) store.SaveFolders();
            if (report.Snippets.Added + report.Snippets.Replaced > 0) store.SaveSnippets();
            foreach (var workspace in changedWorkspaces) WorkspaceStore.Store(workspace);

            if (withSettings) report.SettingsApplied = ApplySettings(store, folder);
        }
        else
        {
            // A dry run merges into throwaway copies and reports what would have happened.
            report.Sessions = Merge(new List<Session>(store.Sessions), remoteSessions, s => s.Id, SessionFingerprint, overwrite);
            report.Folders = Merge(new List<SessionFolder>(store.Folders), remoteFolders, f => f.Id, f => Fingerprint(f), overwrite);
            report.Snippets = Merge(new List<Snippet>(store.Snippets), remoteSnippets, s => s.Id, s => Fingerprint(s), overwrite);
            report.Workspaces = MergeWorkspaces(WorkspaceStore.Load().ToList(), remoteWorkspaces);
        }

        return report;
    }

    /// <summary>Copies the portable preferences across. Machine-local keys in the file are ignored.</summary>
    private static int ApplySettings(DataStore store, string folder)
    {
        var path = Path.Combine(folder, "settings.json");
        if (!File.Exists(path)) return 0;

        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (node is null) return 0;

        foreach (var key in MachineLocalSettings) node.Remove(key);

        // A list key that is absent from the file must keep this machine's value. Deserialising the
        // whole object turned an absent HighlightRules into an empty list and wiped it on import.
        var present = new HashSet<string>(node.Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);

        var incoming = node.Deserialize<AppSettings>(Options);
        if (incoming is null) return 0;

        var applied = 0;
        var target = store.Settings;
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (MachineLocalSettings.Contains(property.Name)) continue;
            if (!present.Contains(property.Name)) continue;
            var before = property.GetValue(target);
            var after = property.GetValue(incoming);
            if (Equals(before, after)) continue;
            property.SetValue(target, after);
            applied++;
        }
        if (applied > 0) store.SaveSettings();
        return applied;
    }

    private static List<T> ReadList<T>(string path, List<string>? errors = null)
    {
        if (!File.Exists(path)) return new List<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new List<T>();
        }
        catch (Exception ex)
        {
            // A corrupt file in a synced folder must not take the import down with it, but "0 added"
            // beside an unreadable file is a lie: name the file and keep the collection empty.
            errors?.Add(Path.GetFileName(path) + " — " + ex.Message);
            return new List<T>();
        }
    }

    // ---------------------------------------------------------------- status

    /// <summary>Read-only comparison: what an import would add, replace or flag.</summary>
    public static SyncReport Status(DataStore store, string folder) =>
        Import(store, folder, overwrite: false, withSettings: false, dryRun: true);
}
