using System.IO;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// L1 safe deploy, from the app. The CLI has its own copy of this sequence inside AgentCli
/// because it dials its own one-shot connections; the Files panel only needs the panel's live
/// SFTP client plus the session for the validator, so it gets this runner instead. Every rule
/// (staged name, validator table, backup name, wording) stays in <see cref="SafeDeploy"/> so the
/// two surfaces cannot disagree about what a deploy means.
///
/// The order is the point: upload beside the target, validate the staged file over the session's
/// own ssh, then swap with the old file kept. A failed validator never touches the original, and a
/// failed swap puts the original back.
/// </summary>
public static class DeployRunner
{
    public sealed class Progress
    {
        public long Sent { get; init; }
        public long Total { get; init; }
        public string Stage { get; init; } = "";
    }

    public static async Task<DeployResult> RunAsync(
        Session session, SftpClient client, string localPath, string remotePath,
        string? validator, bool force, bool dryRun, int timeoutSeconds = 60,
        Action<Progress>? progress = null, CancellationToken token = default)
    {
        var staged = SafeDeploy.StagedPath(remotePath);
        var template = string.IsNullOrWhiteSpace(validator) ? SafeDeploy.ValidatorFor(remotePath) : validator;
        var command = string.IsNullOrWhiteSpace(template) ? "" : SafeDeploy.ValidatorCommand(template!, staged);

        // `validated` is about what HAPPENED, not what would have run: a pre-flight refusal never ran
        // the validator, and the CLI copy of this result already reported that honestly.
        DeployResult Refuse(string error, string? output = null, bool validated = false) => new()
        {
            Ok = false, Session = session.Id, Target = session.DisplayTarget,
            Local = localPath, Remote = remotePath, Staged = staged,
            Validated = validated, Validator = command.Length > 0 ? command : null,
            ValidatorOutput = output, Error = error
        };

        if (command.Length == 0 && !force)
            return Refuse(SafeDeploy.NoValidatorReason(remotePath));

        // A directory is refused before a byte is uploaded. The panel resolves a selected folder to
        // folder/name already; this catches a target that IS one, which the swap would rename away.
        SftpEntry? preflight;
        try { preflight = await client.StatAsync(remotePath); }
        catch (Exception ex) { return Refuse("could not check " + remotePath + ": " + ex.Message); }
        if (preflight?.IsDirectory == true) return Refuse(SafeDeploy.DirectoryTargetReason(remotePath));

        // A dry run answers "would this replace a file, and with which backup name" without a byte
        // leaving the machine. The stat is the only I/O, and a failed stat is not "absent".
        if (dryRun)
        {
            return new DeployResult
            {
                Ok = true, DryRun = true, Session = session.Id, Target = session.DisplayTarget,
                Local = localPath, Remote = remotePath, Staged = staged,
                Validator = command.Length > 0 ? command : null,
                Backup = preflight is not null ? SafeDeploy.BackupPath(remotePath, DateTime.Now) : null
            };
        }

        progress?.Invoke(new Progress { Stage = "uploading", Total = SafeSize(localPath) });
        try
        {
            await client.UploadAsync(localPath, staged,
                (sent, total) => progress?.Invoke(new Progress { Stage = "uploading", Sent = sent, Total = total }),
                token);
        }
        catch (Exception ex)
        {
            return Refuse("upload to " + staged + " failed: " + ex.Message);
        }

        string? output = null;
        if (command.Length > 0)
        {
            progress?.Invoke(new Progress { Stage = "validating", Total = SafeSize(localPath) });
            // The one-shot ssh blocks; keep it off the dispatcher thread.
            var check = await Task.Run(() => AgentCli.RunCommand(session, command, timeoutSeconds), token);
            output = (check.Stdout + check.Stderr).Trim();
            if (!check.Ok)
            {
                try { await client.RemoveAsync(staged); } catch { }
                return Refuse("validator exited " + check.Exit, output, validated: true);
            }
        }

        progress?.Invoke(new Progress { Stage = "swapping", Total = SafeSize(localPath) });
        string? backupPath = null;
        try
        {
            bool exists;
            bool isDirectory;
            try
            {
                var entry = await client.StatAsync(remotePath);
                exists = entry is not null;
                isDirectory = entry?.IsDirectory ?? false;
                if (entry is not null && entry.Permissions is null)
                {
                    // No ATTR_PERMISSIONS: confirm by opening it as a directory (a file cannot be).
                    try { await client.ListDirectoryAsync(remotePath); isDirectory = true; }
                    catch { /* not a directory */ }
                }
            }
            catch (Exception ex)
            {
                try { await client.RemoveAsync(staged); } catch { }
                return Refuse("could not check " + remotePath + ": " + ex.Message, output, validated: command.Length > 0);
            }

            if (isDirectory)
            {
                try { await client.RemoveAsync(staged); } catch { }
                return Refuse(SafeDeploy.DirectoryTargetReason(remotePath), output, validated: command.Length > 0);
            }

            if (exists)
            {
                // Commit the backup name only AFTER the rename returns: if the first rename throws
                // (e.g. that second-resolution name already exists), the catch must not move an older
                // backup over the live file.
                var planned = SafeDeploy.BackupPath(remotePath, DateTime.Now);
                await client.RenameAsync(remotePath, planned);
                backupPath = planned;
            }
            await client.RenameAsync(staged, remotePath);
        }
        catch (Exception ex)
        {
            // Put the original back before reporting: a half-swapped config is worse than a failed deploy.
            if (backupPath is not null)
            {
                try { await client.RenameAsync(backupPath, remotePath); } catch { }
            }
            try { await client.RemoveAsync(staged); } catch { }
            return Refuse("swap failed: " + ex.Message, output, validated: command.Length > 0);
        }

        long bytes = SafeSize(localPath);
        var result = new DeployResult
        {
            Ok = true, Replaced = backupPath is not null, Validated = command.Length > 0,
            Forced = command.Length == 0,
            Session = session.Id, Target = session.DisplayTarget, Local = localPath, Remote = remotePath,
            Staged = staged, Backup = backupPath, Validator = command.Length > 0 ? command : null,
            ValidatorOutput = output, Bytes = bytes
        };

        try
        {
            EvidenceChain.Append("deploy", session, new System.Text.Json.Nodes.JsonObject
            {
                ["remote"] = remotePath,
                ["local"] = localPath,
                ["bytes"] = bytes,
                ["backup"] = backupPath,
                ["validator"] = command,
                ["replaced"] = backupPath is not null,
                ["from"] = "files-panel"
            });
        }
        catch (Exception ex) { result.EvidenceNote = ex.Message; }

        return result;
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
