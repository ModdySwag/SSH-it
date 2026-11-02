using System.IO;
using System.Text;
using System.Windows;

namespace SshIt.Services;

/// <summary>Small rolling file logger. Never throws into the UI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static string Directory
    {
        get
        {
            if (_dir is not null) return _dir;
            try
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SSH-it", "logs");
                System.IO.Directory.CreateDirectory(baseDir);
                _dir = baseDir;
            }
            catch
            {
                _dir = Path.GetTempPath();
            }
            return _dir;
        }
    }

    public static string FilePath => Path.Combine(Directory, "conduit.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(Exception ex, string? context = null)
        => Write("ERROR", (context is null ? "" : context + " :: ") + ex);

    public static void Fatal(Exception? ex)
    {
        Write("FATAL", ex?.ToString() ?? "unknown fatal");
        try
        {
            MessageBox.Show(ex?.ToString() ?? "Fatal error", "SSH-it", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* shutting down */ }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Rotate(path);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* logging must never break the app */ }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 2 * 1024 * 1024) return;
            var old = Path.Combine(Directory, "conduit.previous.log");
            File.Copy(path, old, true);
            File.WriteAllText(path, "");
        }
        catch { }
    }
}
