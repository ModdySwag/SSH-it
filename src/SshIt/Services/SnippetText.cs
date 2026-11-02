using System.Text;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Fills the placeholders in a snippet command from the session it is about to run in.
///
/// A placeholder with no value is left visible — <c>{cwd}</c> before the shell has reported a
/// directory stays <c>{cwd}</c> — because silently replacing it with nothing turns a safe command
/// into a destructive one. Unknown tokens are never touched.
/// </summary>
public static class SnippetText
{
    public static IReadOnlyList<string> Tokens { get; } = new[]
    {
        "{host}", "{user}", "{port}", "{session}", "{cwd}", "{date}", "{time}"
    };

    public static string Expand(string? template, Session? session, string? cwd, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var at = now ?? DateTime.Now;

        var text = new StringBuilder(template);
        Replace(text, "{host}", session?.Host);
        Replace(text, "{user}", session?.User);
        Replace(text, "{port}", session is null ? null : session.Port.ToString());
        Replace(text, "{session}", session?.Name);
        Replace(text, "{cwd}", cwd);
        Replace(text, "{date}", at.ToString("yyyy-MM-dd"));
        Replace(text, "{time}", at.ToString("HH:mm:ss"));
        return text.ToString();
    }

    private static void Replace(StringBuilder text, string token, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        text.Replace(token, value);
        // Accept the shouty form too; both read naturally in a command line.
        text.Replace(token.ToUpperInvariant(), value);
    }
}
