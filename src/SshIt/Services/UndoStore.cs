using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One snapshot taken before a destructive command.</summary>
public sealed class UndoPoint
{
    public string Id { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Target { get; init; } = "";
    public string Path { get; init; } = "";
    public string Snapshot { get; init; } = "";
    public string Kind { get; init; } = "";          // btrfs | zfs | copy
    public string Restore { get; init; } = "";
    public DateTime At { get; init; }
}

/// <summary>
/// R3-18: before a destructive command runs, take a snapshot where the host supports it and attach
/// the restore command to the record. The restore is PRINTED, never executed: a client that silently
/// deletes a directory to "restore" a copy is one bug away from making things worse.
///
/// Honest limits: btrfs only snapshots a subvolume (a plain directory falls back to a copy), zfs
/// snapshots the whole dataset, and the copy route costs as much space as the data. The window and
/// the receipt say which one happened.
/// </summary>
public static class UndoStore
{
    public static string StorePath => Path.Combine(DataStore.RootPath, "undo", "snapshots.json");

    private static readonly string[] Forbidden = { "/", "/dev", "/proc", "/sys", "/boot", "/etc/ssh" };

    /// <summary>The directory a pattern would affect: everything before the first glob character.</summary>
    public static string BasePath(string pattern)
    {
        var text = (pattern ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return "/";
        var glob = text.IndexOfAny(new[] { '*', '?', '[', '{' });
        if (glob >= 0) text = text[..glob];
        if (text.Length == 0) return "/";
        if (!text.StartsWith('/')) return text;
        var slash = text.LastIndexOf('/');
        return slash <= 0 ? "/" : text[..slash];
    }

    public static string? Refusal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "no path to snapshot";
        if (Forbidden.Contains(path.TrimEnd('/'))) return "refusing to snapshot " + path + " (system path)";
        if (path.Count(c => c == '/') < 2) return "refusing to snapshot " + path + " (too close to the root)";
        return null;
    }

    /// <summary>Which snapshot command this filesystem gets, and what restores it.</summary>
    public static (string Command, string Kind, string Restore) Plan(string path, string? fileSystem, string stamp)
    {
        var fs = (fileSystem ?? "").ToLowerInvariant();
        var quoted = "'" + path.Replace("'", "'\\''") + "'";

        if (fs.Contains("btrfs"))
            return ($"btrfs subvolume snapshot -r {quoted} {quoted}.sshit-{stamp} 2>/dev/null || cp -a --reflink=auto {quoted} {quoted}.sshit-{stamp}",
                "btrfs",
                $"rm -rf {quoted} && btrfs subvolume snapshot {quoted}.sshit-{stamp} {quoted}");

        if (fs.Contains("zfs"))
            return ($"zfs snapshot $(df --output=source {quoted} | tail -1)@sshit-{stamp}",
                "zfs",
                $"zfs rollback $(df --output=source {quoted} | tail -1)@sshit-{stamp}");

        return ($"cp -a --reflink=auto {quoted} {quoted}.sshit-{stamp}",
            "copy",
            $"rm -rf {quoted} && cp -a {quoted}.sshit-{stamp} {quoted}");
    }

    public static List<UndoPoint> List(string? sessionId = null, string? storePath = null)
    {
        storePath ??= StorePath;
        if (!File.Exists(storePath)) return new List<UndoPoint>();
        try
        {
            var points = JsonSerializer.Deserialize<List<UndoPoint>>(File.ReadAllText(storePath)) ?? new();
            return sessionId is null ? points : points.Where(p => p.SessionId == sessionId).ToList();
        }
        catch { return new List<UndoPoint>(); }
    }

    /// <summary>
    /// Takes the snapshot on the host, records it, and puts a row in the evidence chain. Returns the
    /// undo point, or a point with Snapshot empty and Error set.
    /// </summary>
    public static async Task<(UndoPoint? Point, string? Error)> SnapshotAsync(
        Session session, string pattern, CancellationToken token, string? storePath = null)
    {
        var path = BasePath(pattern);
        var refusal = Refusal(path);
        if (refusal is not null) return (null, refusal);

        var fileSystem = await BlastRadius.FileSystemOfAsync(session, path, token).ConfigureAwait(false);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var plan = Plan(path, fileSystem, stamp);

        var run = await BlockDiff.RunAsync(session, plan.Command, token).ConfigureAwait(false);
        if (run.Exit != 0)
            return (null, "snapshot failed (exit " + run.Exit + "): "
                          + FirstLine(run.Error.Length > 0 ? run.Error : run.Output));

        var point = new UndoPoint
        {
            Id = Guid.NewGuid().ToString("n")[..10],
            SessionId = session.Id,
            Target = session.DisplayTarget,
            Path = path,
            Snapshot = path + ".sshit-" + stamp,
            Kind = plan.Kind,
            Restore = plan.Restore,
            At = DateTime.Now
        };

        Save(point, storePath);
        EvidenceChain.Append("snapshot", session, new JsonObject
        {
            ["path"] = point.Path,
            ["snapshot"] = point.Snapshot,
            ["kind"] = point.Kind,
            ["restore"] = point.Restore,
            ["id"] = point.Id
        });

        return (point, null);
    }

    private static void Save(UndoPoint point, string? storePath)
    {
        try
        {
            storePath ??= StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var all = List(null, storePath);
            all.Add(point);
            File.WriteAllText(storePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("undo: could not record the snapshot: " + ex.Message);
        }
    }

    public static string Describe(UndoPoint point) =>
        $"{point.Id}  {point.At:yyyy-MM-dd HH:mm:ss}  {point.Target}  {point.Kind}  {point.Path}"
        + Environment.NewLine + "  snapshot: " + point.Snapshot
        + Environment.NewLine + "  restore:  " + point.Restore;

    private static string FirstLine(string text) =>
        text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "no output";
}
