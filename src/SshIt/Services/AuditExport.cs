using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>What an audit bundle contains, and the hashes that make it checkable.</summary>
public sealed class AuditBundle
{
    public string Path { get; init; } = "";
    public string Device { get; init; } = "";
    public int EvidenceRows { get; init; }
    public int LedgerRows { get; init; }
    public int Sessions { get; init; }
    /// <summary>False when the chain did not verify: the archive is still written, but it is not "ok".</summary>
    public bool Ok { get; init; } = true;
    public string EvidenceVerdict { get; init; } = "";
    /// <summary>Rows that verified; below <see cref="EvidenceRows"/> when the chain is broken or absent.</summary>
    public int VerifiedRows { get; init; }
    /// <summary>True when ledger output travelled with the bundle (scrubbed, and only when asked for).</summary>
    public bool LedgerOutput { get; init; }
    public long Bytes { get; init; }
    public List<string> Members { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(new
    {
        ok = Ok, path = Path, device = Device, bytes = Bytes,
        evidenceRows = EvidenceRows, verifiedRows = VerifiedRows, ledgerRows = LedgerRows, sessions = Sessions,
        ledgerOutput = LedgerOutput, evidenceVerdict = EvidenceVerdict, members = Members
    });

    public string ToText() =>
        "wrote " + Path + " (" + Bytes + " bytes)" + Environment.NewLine
        + "  evidence  " + EvidenceRows + " row(s), " + VerifiedRows + " verified, " + EvidenceVerdict + Environment.NewLine
        + "  ledger    " + LedgerRows + " row(s), "
        + (LedgerOutput ? "output included (secrets scrubbed)" : "output omitted (--with-output includes it, scrubbed)")
        + Environment.NewLine
        + "  sessions  " + Sessions + " (names and targets only, no key paths)" + Environment.NewLine
        + "  members   " + string.Join(", ", Members) + Environment.NewLine
        + "manifest.json carries the SHA-256 of every member, so the bundle proves itself.";
}

/// <summary>
/// B8 audit export: one zip a person can attach to a ticket or hand to an auditor. It carries the signed
/// evidence chain, the command ledger, a redacted session list, the policy's status and the chain's own
/// verification verdict, with a manifest hashing each member.
///
/// It never carries a secret: no passwords (they live in Credential Manager), no key material, no key
/// paths, no proxy commands. Redaction is a whitelist, not a blacklist, so a new field on Session cannot
/// leak by being forgotten.
/// </summary>
public static class AuditExport
{
    public const int Version = 1;

    /// <summary>The only session fields that leave the machine, chosen one by one on purpose.</summary>
    public static JsonObject RedactSession(Session session) => new()
    {
        ["id"] = session.Id,
        ["name"] = session.Name,
        ["target"] = session.DisplayTarget,
        ["host"] = session.Host,
        ["port"] = session.Port,
        ["user"] = session.User,
        ["kind"] = session.Kind,
        ["colorTag"] = session.ColorTag,
        ["tags"] = new JsonArray((session.Tags ?? new List<string>()).Select(tag => (JsonNode)JsonValue.Create(tag)!).ToArray())
    };

    /// <summary>
    /// The only ledger fields that leave the machine. Output is free text the user never wrote for an
    /// auditor: it is omitted unless the caller asks for it, and scrubbed even then.
    /// </summary>
    public static JsonObject RedactLedgerRow(LedgerRow row, bool withOutput)
    {
        var redacted = new JsonObject
        {
            ["at"] = row.At,
            ["session"] = row.Session,
            ["host"] = row.Host,
            ["user"] = row.User,
            ["command"] = RedactText(row.Command),
            ["exit"] = row.Exit,
            ["durationMs"] = row.DurationMs,
            ["cwd"] = row.Cwd,
            ["outputBytes"] = row.OutputBytes
        };
        if (withOutput) redacted["output"] = RedactText(row.Output);
        else redacted["outputOmitted"] = true;
        return redacted;
    }

    // A blacklist cannot know every secret shape, so these cover the ones a shell session leaves in
    // plain sight. The real protection is that output is opt-in and the ledger is a whitelist.
    // A secret value may be bare, single-quoted or double-quoted; a group that excluded quotes
    // skipped `--password='S3cret'` entirely and shipped it verbatim.
    // A quoted value is bounded rather than newline-excluded: a 200-character ceiling keeps an
    // unterminated quote from swallowing the rest of the row, and the pattern needs no escapes.
    private const string Value = "'[^']{0,200}'|\"[^\"]{0,200}\"|[^\\s'\"]+";

    private static readonly Regex[] SecretShapes =
    {
        new(@"(?i)(?:(?:--)?(?:password|passwd|passphrase|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)\s*[=:]\s*)(?<secret>" + Value + ")",
            RegexOptions.Compiled),
        new(@"(?i)authorization\s*:\s*bearer\s+(?<secret>" + Value + ")", RegexOptions.Compiled),
        new(@"\b(?:ssh|https?)://[^/\s:@]+:(?<secret>[^@\s'""]+)@", RegexOptions.Compiled),
        new(@"(?i)\bsshpass\s+-p\s*(?<secret>" + Value + ")", RegexOptions.Compiled),
        // `mysql -u root -pSECRET` has no `=` to anchor on; only the client's own -p flag is treated
        // this way, so `-print` and `-p 8080` are left alone. `-p` with nothing after it is a prompt.
        new(@"(?i)\b(?:mysql|mysqldump|mariadb)\b[^\n]*?\s-p(?<secret>" + Value + ")", RegexOptions.Compiled),
        // `export AWS_SECRET_ACCESS_KEY=…` / `GITHUB_TOKEN=…`. The keyword must be a whole
        // underscore-delimited segment: `PasswordAuthentication=no` is a setting, not a password,
        // and `TOKENIZER=bert-base` is a model name.
        new(@"(?i)(?:^|[^A-Z0-9_])(?:[A-Z0-9]*_)?(?:SECRET|TOKEN|PASSWORD|PASSWD|APIKEY|API_KEY)(?:_[A-Z0-9_]*)?\s*=\s*(?<secret>" + Value + ")",
            RegexOptions.Compiled)
    };

    private static readonly Regex PemBlock = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Masks the values that look like credentials; everything else is left readable.</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = PemBlock.Replace(text, "[private key redacted]");
        foreach (var shape in SecretShapes)
            result = shape.Replace(result, match =>
            {
                var group = match.Groups["secret"];
                if (group.Length == 0) return match.Value;
                // Replace the GROUP SPAN, not every occurrence of its text: with a one-character
                // secret, `match.Value.Replace` also rewrote the keyword (--password=a -> --p***ssword=***).
                var start = group.Index - match.Index;
                return match.Value[..start] + "***" + match.Value[(start + group.Length)..];
            });
        return result;
    }

    /// <summary>
    /// Replaces a local path with its bare file name. A message can quote the directory instead of
    /// the file (an enumeration error names the folder), can use forward slashes, or can arrive with
    /// the \\?\ long-path prefix — every spelling has to go, not just the one the caller passed.
    /// </summary>
    public static string RedactPath(string? text, string path)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(path)) return text ?? "";
        var result = text;
        var name = Path.GetFileName(path);
        foreach (var variant in new[] { path, @"\\?\" + path })
        {
            if (string.IsNullOrEmpty(variant)) continue;
            result = result.Replace(variant, name, StringComparison.OrdinalIgnoreCase)
                           .Replace(variant.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            foreach (var variant in new[] { directory, @"\\?\" + directory })
            {
                if (string.IsNullOrEmpty(variant)) continue;
                result = result.Replace(variant, "…", StringComparison.OrdinalIgnoreCase)
                               .Replace(variant.Replace('\\', '/'), "…", StringComparison.OrdinalIgnoreCase);
            }
        }
        return result;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the bundle in a temp directory and zips it into place, so a half-written archive never
    /// exists under the name someone is about to hand to an auditor.
    /// </summary>
    public static AuditBundle Export(IReadOnlyList<Session> sessions, string targetPath, DateTime? since, int limit,
                                     string appVersion, bool withOutput = false, string? chainPath = null)
    {
        var target = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        var staging = Path.Combine(Path.GetTempPath(), "sshit-audit-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(staging);

        try
        {
            var evidenceSource = chainPath ?? EvidenceChain.ChainPath;
            var evidenceCopy = Path.Combine(staging, "evidence.jsonl");
            if (File.Exists(evidenceSource)) File.Copy(evidenceSource, evidenceCopy);
            else File.WriteAllText(evidenceCopy, "");

            var verification = EvidenceChain.Verify(evidenceSource);
            // `verification.Count` is 0 for a missing or unreadable chain; the copied file can still hold
            // thousands of rows. An auditor must be able to tell "empty" from "could not verify".
            var evidenceLines = 0;
            try { evidenceLines = File.ReadLines(evidenceCopy).Count(line => !string.IsNullOrWhiteSpace(line)); }
            catch { }
            // A verdict that says "no evidence chain at C:\Users\<name>\…" hands the OS username to
            // whoever receives the bundle; only the bare file name leaves the machine.
            var firstBreak = RedactPath(verification.FirstBreak, evidenceSource);
            // Filter while reading, not after the limit — see CommandLedger.Tail.
            var ledgerRows = CommandLedger.Tail(limit, CommandLedger.StorePath, since);
            var ledgerPath = Path.Combine(staging, "ledger.jsonl");
            File.WriteAllText(ledgerPath, string.Join(Environment.NewLine,
                ledgerRows.Select(row => RedactLedgerRow(row, withOutput).ToJsonString()))
                + (ledgerRows.Count > 0 ? Environment.NewLine : ""));

            var sessionsPath = Path.Combine(staging, "sessions.json");
            var redactedSessions = new JsonArray(sessions.Select(session => (JsonNode)RedactSession(session)).ToArray());
            File.WriteAllText(sessionsPath, redactedSessions.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var policyPath = Path.Combine(staging, "policy.json");
            var policy = AgentPolicyFile.Load();
            File.WriteAllText(policyPath, new JsonObject
            {
                ["ok"] = policy.Ok,
                // An error can quote the path the loader tried; only the bare file name leaves the machine.
                ["error"] = RedactPath(policy.Error, AgentPolicyFile.PolicyPath),
                ["file"] = Path.GetFileName(AgentPolicyFile.PolicyPath),
                ["configured"] = policy.Rules is not null
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var verifyPath = Path.Combine(staging, "verify.json");
            File.WriteAllText(verifyPath, new JsonObject
            {
                ["ok"] = verification.Ok,
                ["rows"] = verification.Count,
                ["firstBreak"] = firstBreak,
                ["breakLine"] = verification.BreakLine,
                ["key"] = verification.KeyFingerprint
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var members = new[] { "evidence.jsonl", "ledger.jsonl", "sessions.json", "policy.json", "verify.json" };
            var hashes = new JsonObject();
            foreach (var member in members)
                hashes[member] = Sha256File(Path.Combine(staging, member));

            var manifest = new JsonObject
            {
                ["version"] = Version,
                ["app"] = appVersion,
                ["exportedUtc"] = DateTime.UtcNow.ToString("O"),
                ["device"] = Environment.MachineName,
                // `--since` filters the LEDGER only; evidence.jsonl is the whole chain. Say so, or a
                // reader assumes the bundle is time-scoped and counts rows that predate the window.
                ["ledgerSince"] = since?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["evidenceScope"] = "full chain",
                ["counts"] = new JsonObject
                {
                    ["evidenceRows"] = evidenceLines,
                    ["verifiedRows"] = verification.Count,
                    ["ledgerRows"] = ledgerRows.Count,
                    ["sessions"] = sessions.Count
                },
                ["evidenceVerdict"] = verification.Ok ? "ok" : "broken: " + firstBreak,
                ["ledger"] = ledgerRows.Count == 0
                    ? "no rows"
                    : (withOutput ? "rows, output included (secrets scrubbed)" : "rows, output omitted (--with-output includes it, scrubbed)"),
                ["hashes"] = hashes,
                ["never"] = "passwords, key material, key paths, proxy commands, leases, ledger output unless asked for"
            };
            var manifestPath = Path.Combine(staging, "manifest.json");
            File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            if (File.Exists(target)) File.Delete(target);
            ZipFile.CreateFromDirectory(staging, target, CompressionLevel.Optimal, includeBaseDirectory: false);

            return new AuditBundle
            {
                Path = target,
                Device = Environment.MachineName,
                EvidenceRows = evidenceLines,
                VerifiedRows = verification.Count,
                LedgerRows = ledgerRows.Count,
                Sessions = sessions.Count,
                Ok = verification.Ok,
                EvidenceVerdict = verification.Ok ? "chain verified" : "chain BROKEN: " + firstBreak,
                LedgerOutput = withOutput,
                Bytes = new FileInfo(target).Length,
                Members = members.Append("manifest.json").ToList()
            };
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    }
}
