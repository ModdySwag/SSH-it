using System.IO;
using System.Text.Json;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Workspaces on disk: `%APPDATA%\SSH-it\workspaces.json`, a name-keyed list. Saving under an existing
/// name replaces that workspace (keeping its Created stamp) rather than adding a second one — two
/// "prod" entries would be a bug the user cannot see.
/// </summary>
public static class WorkspaceStore
{
    public static string StorePath => Path.Combine(DataStore.RootPath, "workspaces.json");

    public static List<Workspace> Load(string? path = null)
    {
        var file = path ?? StorePath;
        if (!File.Exists(file)) return new List<Workspace>();
        try
        {
            var list = JsonSerializer.Deserialize<List<Workspace>>(File.ReadAllText(file));
            return list ?? new List<Workspace>();
        }
        catch
        {
            // A damaged store reads as empty; saving again starts a clean one.
            return new List<Workspace>();
        }
    }

    public static void Save(List<Workspace> workspaces, string? path = null)
    {
        var file = path ?? StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(workspaces, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Adds or replaces by name (case-insensitive) and returns the stored copy.</summary>
    public static Workspace Store(Workspace workspace, string? path = null)
    {
        var all = Load(path);
        var existing = all.FirstOrDefault(w => string.Equals(w.Name, workspace.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            workspace.Created = string.IsNullOrEmpty(existing.Created) ? workspace.Created : existing.Created;
            all.Remove(existing);
        }
        all.Add(workspace);
        Save(all, path);
        return workspace;
    }

    public static Workspace? Find(string name, string? path = null)
        => Load(path).FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool Remove(string name, string? path = null)
    {
        var all = Load(path);
        var removed = all.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        Save(all, path);
        return true;
    }

    /// <summary>For the CLI and the palette: one line per workspace, or the JSON array.</summary>
    public static string ListText(bool json, string? path = null)
    {
        var all = Load(path);
        if (json) return JsonSerializer.Serialize(all);
        if (all.Count == 0) return "no workspaces saved." + Environment.NewLine;
        return string.Join(Environment.NewLine, all.Select(w => w.Summary())) + Environment.NewLine;
    }
}
