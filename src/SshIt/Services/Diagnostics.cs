using System.IO;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// A short, private summary of the machine and the app for a bug report: version, OS, how many
/// sessions and tabs, the theme, and where the log is. No host names, no user names, no command text —
/// a diagnostics blurb the user can paste without leaking an inventory.
/// </summary>
public static class Diagnostics
{
    public static string Version()
    {
        var version = typeof(Diagnostics).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.Major + "." + version.Minor + "." + version.Build;
    }

    public static string Summary(int sessions, int tabs, string? theme = null)
    {
        var text = new StringBuilder();
        text.Append("SSH-it ").Append(Version())
            .Append(" (").Append(Environment.Is64BitProcess ? "win-x64" : "win-x86").AppendLine(")");
        text.AppendLine("OS: " + Environment.OSVersion.VersionString);
        text.AppendLine("Runtime: .NET " + Environment.Version);
        text.AppendLine("Sessions: " + sessions + " saved, " + tabs + " open tab(s)");
        if (!string.IsNullOrWhiteSpace(theme)) text.AppendLine("Theme: " + theme);
        text.AppendLine("Log: " + Log.Directory);
        return text.ToString();
    }
}
