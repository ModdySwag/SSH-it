using System.IO;

namespace SshIt.Services;

/// <summary>One row of the local half of the Files panel: a directory or a file on this machine.</summary>
public sealed class LocalEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }

    public string SizeText => IsDirectory ? "" : SftpEntry.FormatSize(Size);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    /// <summary>A row announces its file name to a screen reader, not the type name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The local side of the Files panel. Kept out of the panel so the ordering and the
/// parent rules can be checked without a window: the panel is only wiring on top.
/// </summary>
public static class LocalFiles
{
    /// <summary>Lists a directory the way the remote pane lists one: folders first, then names.</summary>
    public static List<LocalEntry> List(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException(path);

        var entries = new List<LocalEntry>();
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            // System files (desktop.ini, pagefile.sys) are noise in a transfer pane. A plain
            // hidden file is still listed: it is often exactly what someone came to fetch.
            if ((info.Attributes & FileAttributes.System) != 0) continue;
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            entries.Add(new LocalEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = isDirectory,
                Size = isDirectory ? 0 : ((FileInfo)info).Length,
                Modified = info.LastWriteTime
            });
        }
        return Order(entries);
    }

    /// <summary>Directories first, then names, case-insensitively — the remote list's order.</summary>
    public static List<LocalEntry> Order(IEnumerable<LocalEntry> entries) => entries
        .OrderByDescending(e => e.IsDirectory)
        .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>
    /// The parent directory, or null when there is nowhere to go: a drive root ("C:\"),
    /// a bare "/" or "\\", or a UNC share root ("\\server\share").
    /// </summary>
    public static string? Parent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return null;                       // "/" or "\" on its own
        if (trimmed.Length == 2 && trimmed[1] == ':') return null;   // "C:\" arrived as "C:"

        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent)) return null;
        // GetDirectoryName("\\server\share") is "\\server", which is not a browsable parent.
        if (parent.StartsWith(@"\\", StringComparison.Ordinal) && parent.IndexOf('\\', 2) < 0) return null;
        return parent;
    }

    /// <summary>Where the local pane starts when settings hold no usable path.</summary>
    public static string DefaultPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) return Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }
}
