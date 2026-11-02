using System.IO;
using System.Text.Json;

namespace SshIt.Services;

/// <summary>What one deploy did, in a shape both a person and a script can read.</summary>
public sealed class DeployResult
{
    public bool Ok { get; init; }
    public bool Replaced { get; init; }
    public bool Validated { get; init; }

    /// <summary>Written without a validator because --force said so. Never reads as validated.</summary>
    public bool Forced { get; init; }
    public bool DryRun { get; init; }
    public string Session { get; init; } = "";
    public string Target { get; init; } = "";
    public string Local { get; init; } = "";
    public string Remote { get; init; } = "";
    public string Staged { get; init; } = "";
    public string? Backup { get; init; }
    public string? Validator { get; init; }
    public string? ValidatorOutput { get; init; }
    public long Bytes { get; init; }
    public string? Error { get; init; }

    /// <summary>Set when the deploy worked but its evidence row could not be written.</summary>
    public string? EvidenceNote { get; set; }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        ok = Ok,
        replaced = Replaced,
        validated = Validated,
        forced = Forced,
        dryRun = DryRun,
        session = Session,
        target = Target,
        local = Local,
        remote = Remote,
        staged = Staged,
        backup = Backup,
        validator = Validator,
        validatorOutput = ValidatorOutput,
        bytes = Bytes,
        evidenceNote = EvidenceNote,
        error = Error
    });

    public string ToText()
    {
        if (DryRun)
            return "would deploy " + Local + " -> " + Remote
                   + (Validator is { Length: > 0 } ? Environment.NewLine + "  validated first with: " + Validator : "")
                   + (Backup is { Length: > 0 } ? Environment.NewLine + "  replacing the current file (backup would be " + Backup + ")" : Environment.NewLine + "  the target does not exist yet")
                   + Environment.NewLine + "  dry run: nothing was uploaded.";
        if (!Ok)
            return "deploy refused: " + Error + Environment.NewLine
                   + (ValidatorOutput is { Length: > 0 } ? ValidatorOutput.TrimEnd() + Environment.NewLine : "")
                   + "The original was left untouched.";

        var text = "deployed " + Local + " -> " + Remote + " (" + Bytes + " bytes)";
        if (Forced) text += Environment.NewLine + "  forced: no validator ran \u2014 this file was NOT checked before it replaced the old one";
        if (Replaced) text += Environment.NewLine + "  previous file kept at " + Backup;
        if (Validator is { Length: > 0 }) text += Environment.NewLine + "  validated first: " + Validator;
        if (EvidenceNote is { Length: > 0 })
            text += Environment.NewLine + "  warning: the file was deployed, but the evidence row could not be written (" + EvidenceNote + ")";
        return text;
    }
}

/// <summary>
/// L1 safe deploy: upload beside the target, validate it over the session's own ssh, then move it into
/// place with the old file kept. The point is the refusal — a client that writes straight over sshd_config
/// is one typo from locking everyone out, and no competing client validates before it replaces.
///
/// The rules here are pure so the table, the paths and the wording are testable; the I/O lives in the CLI.
/// </summary>
public static class SafeDeploy
{
    public const string StagedSuffix = ".sshit-new";
    public const string BackupMarker = ".sshit-bak-";

    public static string StagedPath(string remotePath) => remotePath + StagedSuffix;

    /// <summary>
    /// The stamp is formatted with the invariant culture so a non-Gregorian locale cannot produce a
    /// 2569 backup name; callers pass LOCAL time so the name matches "the deploy I just did".
    /// </summary>
    public static string BackupPath(string remotePath, DateTime when) =>
        remotePath + BackupMarker + when.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The validator known for a path, as a command over {file}. Null means "this client does not know how
    /// to check that file" — the caller must supply one or force the write, never guess.
    /// </summary>
    public static string? ValidatorFor(string remotePath)
    {
        var path = (remotePath ?? "").Replace('\\', '/');
        var file = path.Split('/').LastOrDefault() ?? path;

        if (path == "/etc/sudoers" || path.StartsWith("/etc/sudoers.d/", StringComparison.Ordinal))
            return "visudo -c -f {file}";
        if (file.Equals("sshd_config", StringComparison.OrdinalIgnoreCase)
            || file.Equals("ssh_config", StringComparison.OrdinalIgnoreCase))
            return "sshd -t -f {file}";
        if (file.Equals("nginx.conf", StringComparison.OrdinalIgnoreCase))
            return "nginx -t -c {file}";
        if (path.StartsWith("/etc/systemd/", StringComparison.Ordinal) && file.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
            return "systemd-analyze verify {file}";
        if (file.EndsWith(".service", StringComparison.OrdinalIgnoreCase) && path.Contains("/systemd/", StringComparison.Ordinal))
            return "systemd-analyze verify {file}";

        return null;
    }

    /// <summary>
    /// Substitutes {file}. A template that never mentions {file} gets the path appended, so a bare
    /// "bash -n" behaves the way a person expects rather than failing silently.
    /// </summary>
    public static string ValidatorCommand(string template, string stagedPath)
    {
        var command = (template ?? "").Trim();
        if (command.Length == 0) return "";
        var quoted = ShellQuote(stagedPath);
        return command.Contains("{file}", StringComparison.Ordinal)
            ? command.Replace("{file}", quoted)
            : command + " " + quoted;
    }

    /// <summary>
    /// Single-quote a path for the remote shell. Without this a staged name containing a space breaks the
    /// validator, and one containing $(...) EXECUTES on the host — a deploy path is data, never code.
    /// </summary>
    public static string ShellQuote(string value) => "'" + (value ?? "").Replace("'", "'\''") + "'";

    /// <summary>What to tell someone whose path has no known validator.</summary>
    public static string NoValidatorReason(string remotePath) =>
        "no validator knows " + remotePath
        + " — pass --validator \"<command using {file}>\" or --force to write it unvalidated";

    /// <summary>
    /// A deploy target must be a file. Renaming a directory to a backup name moves the directory
    /// itself, and the staged file cannot take its place — so a directory is refused before a byte
    /// is uploaded. The Files panel resolves a selected folder to <c>folder/name</c> first; this
    /// check is for a target that IS a directory.
    /// </summary>
    public static string DirectoryTargetReason(string remotePath) =>
        remotePath.TrimEnd('/') + " is a directory — deploy a file path, or select a folder in the Files panel to deploy into it";

    /// <summary>The remote path a selected local file deploys to. A selected remote FILE is the
    /// target itself; a selected folder (or the folder the listing is showing) takes the local
    /// name. Null when there is no name to deploy — the dialog refuses rather than guessing.</summary>
    public static string? TargetFor(string? remoteSelection, bool selectionIsDirectory,
                                    string? remoteFolder, string localPath)
    {
        var name = Path.GetFileName(localPath ?? "");
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (!string.IsNullOrWhiteSpace(remoteSelection))
            return selectionIsDirectory ? JoinRemote(remoteSelection!, name) : remoteSelection;

        return string.IsNullOrWhiteSpace(remoteFolder) ? null : JoinRemote(remoteFolder!, name);
    }

    /// <summary>Joins a folder and a name without doubling the slash — sftp paths are POSIX even
    /// when the local half of the panel is a Windows drive.</summary>
    public static string JoinRemote(string folder, string name) =>
        (folder.EndsWith('/') ? folder : folder + "/") + name;
}
