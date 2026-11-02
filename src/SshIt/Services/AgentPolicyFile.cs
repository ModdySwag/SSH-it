using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One key allowed to countersign a destructive-command token.</summary>
public sealed class AgentApprover
{
    public string Name { get; set; } = "";
    public string Pub { get; set; } = "";
}

/// <summary>
/// R3-07: the signed policy — which verbs, hosts and paths the agent surface may touch, and whether a
/// destructive command needs a second signature. The file is signed with the device key, so an edited
/// rule set stops verifying and the surface closes rather than silently widening.
/// </summary>
public sealed class AgentRuleSet
{
    public List<string> Verbs { get; set; } = new();        // empty = every verb
    public List<string> Hosts { get; set; } = new();        // empty = every host; globs
    public List<string> AllowPaths { get; set; } = new();   // empty = every path
    public List<string> DenyPaths { get; set; } = new();    // wins over allow
    public string Destructive { get; set; } = "token";      // token | allow | deny
    public List<AgentApprover> Approvers { get; set; } = new();
    public DateTimeOffset Updated { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// The policy file and its enforcement. Honest limits, printed where the policy is shown: this is a
/// file the same user can rewrite and re-sign, so it constrains an agent that obeys the surface and
/// leaves a signed trail when it does not — it is not a sandbox. A destructive command needs a token
/// countersigned by a key registered as an approver; whether that key belongs to a second person is
/// outside what the software can know.
/// </summary>
public static class AgentPolicyFile
{
    /// <summary>Verbs whose argument is a remote path the policy can allow or deny.</summary>
    public static readonly string[] PathVerbs = { "sftp-list", "sftp-get", "sftp-put", "sftp-mirror", "snapshot" };

    public static string DirectoryPath => Path.Combine(DataStore.RootPath, "policy");

    public static string PolicyPath => Path.Combine(DirectoryPath, "policy.json");

    // ---------------------------------------------------------------- reading and writing

    public sealed class Loaded
    {
        public AgentRuleSet? Rules { get; set; }
        public string? Error { get; set; }
        public bool Ok => Error is null;
    }

    public static Loaded Load(string? path = null)
    {
        path ??= PolicyPath;
        var result = new Loaded();
        if (!File.Exists(path)) return result;               // no policy: nothing is restricted

        JsonObject document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new InvalidOperationException("the file is not a JSON object");
        }
        catch (Exception ex)
        {
            result.Error = "the policy file is unreadable: " + ex.Message;
            return result;
        }

        if (document["policy"] is not JsonObject policy)
        {
            result.Error = "the policy file has no policy object";
            return result;
        }

        var signature = document["sig"]?.GetValue<string>() ?? "";
        var deviceKey = EvidenceChain.DevicePublicKey();
        if (deviceKey.Length == 0 || !EvidenceChain.VerifyCanonical(deviceKey, signature,
                EvidenceChain.CanonicalJson(policy)))
        {
            result.Error = "the policy does not verify against this machine's device key — it was edited or "
                           + "signed elsewhere. Repair: SSH-it.exe --policy-set <file>, or --policy-clear --confirm.";
            return result;
        }

        result.Rules = FromJson(policy);
        return result;
    }

    private static AgentRuleSet FromJson(JsonObject policy)
    {
        var rules = new AgentRuleSet
        {
            Destructive = policy["destructive"]?.GetValue<string>() ?? "token",
            Note = policy["note"]?.GetValue<string>() ?? "",
            Updated = ParseTime(policy["updated"])
        };
        if (policy["verbs"] is JsonArray verbs) rules.Verbs = Strings(verbs);
        if (policy["hosts"] is JsonArray hosts) rules.Hosts = Strings(hosts);
        if (policy["allowPaths"] is JsonArray allow) rules.AllowPaths = Strings(allow);
        if (policy["denyPaths"] is JsonArray deny) rules.DenyPaths = Strings(deny);
        if (policy["approvers"] is JsonArray approvers)
            foreach (var entry in approvers.OfType<JsonObject>())
                rules.Approvers.Add(new AgentApprover
                {
                    Name = entry["name"]?.GetValue<string>() ?? "",
                    Pub = entry["pub"]?.GetValue<string>() ?? ""
                });
        return rules;
    }

    private static List<string> Strings(JsonArray array)
        => array.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();

    private static DateTimeOffset ParseTime(JsonNode? node)
    {
        try
        {
            return DateTimeOffset.Parse(node?.GetValue<string>() ?? "", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }
        catch { return DateTimeOffset.UtcNow; }
    }

    private static JsonObject ToJson(AgentRuleSet rules) => new()
    {
        ["verbs"] = new JsonArray(rules.Verbs.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        ["hosts"] = new JsonArray(rules.Hosts.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        ["allowPaths"] = new JsonArray(rules.AllowPaths.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        ["denyPaths"] = new JsonArray(rules.DenyPaths.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        ["destructive"] = rules.Destructive,
        ["approvers"] = new JsonArray(rules.Approvers.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.Name,
            ["pub"] = a.Pub
        }).ToArray()),
        ["updated"] = rules.Updated.ToUniversalTime().ToString("O"),
        ["note"] = rules.Note
    };

    /// <summary>Signs and writes the rule set. Throws when the device key is unavailable.</summary>
    public static void Save(AgentRuleSet rules, string? path = null)
    {
        path ??= PolicyPath;
        var policy = ToJson(rules);
        var signature = EvidenceChain.Sign(EvidenceChain.CanonicalJson(policy))
                        ?? throw new InvalidOperationException("the device key is unavailable, so the policy cannot be signed");
        var document = new JsonObject
        {
            ["version"] = 1,
            ["policy"] = policy,
            ["alg"] = EvidenceChain.Algorithm,
            ["sig"] = signature.Signature
        };
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Imports a JSON rule file (either the policy object itself or {"policy": …}), validates it and
    /// signs it with this machine's key. The import is the deliberate act; the file on disk is only
    /// ever the signed form.
    /// </summary>
    public static AgentRuleSet Set(string sourcePath, string? note = null, string? path = null)
    {
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException("no such file: " + sourcePath);

        JsonObject policy;
        try
        {
            policy = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject
                     ?? throw new InvalidOperationException("the file is not a JSON object");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("the rule file is not valid JSON: " + ex.Message);
        }

        if (policy["policy"] is JsonObject wrapped) policy = wrapped;

        var rules = FromJson(policy);
        if (note is not null) rules.Note = note;
        rules.Updated = DateTimeOffset.UtcNow;
        Validate(rules);
        Save(rules, path);
        return rules;
    }

    private static void Validate(AgentRuleSet rules)
    {
        var unknown = rules.Verbs.Where(v => !AgentLease.KnownVerbs.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("unknown verb(s) in the policy: " + string.Join(", ", unknown)
                                                + " — known: " + string.Join(", ", AgentLease.KnownVerbs));
        if (rules.Destructive is not ("token" or "allow" or "deny"))
            throw new InvalidOperationException("destructive must be token, allow or deny");
        foreach (var approver in rules.Approvers)
        {
            if (!Regex.IsMatch(approver.Name, "^[a-z0-9][a-z0-9_-]{0,31}$"))
                throw new InvalidOperationException("approver name must be [a-z0-9_-], 1..32 chars: '" + approver.Name + "'");
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(approver.Pub), out _);
            }
            catch
            {
                throw new InvalidOperationException("approver '" + approver.Name + "' has no usable public key");
            }
        }
    }

    public static (bool Ok, string Detail) Verify(string? path = null)
    {
        var loaded = Load(path);
        if (!loaded.Ok) return (false, loaded.Error!);
        return loaded.Rules is null
            ? (true, "no policy — every session follows the agent gate and its leases")
            : (true, "policy verifies: " + Describe(loaded.Rules));
    }

    public static (bool Ok, string Detail) Clear(string? path = null)
    {
        path ??= PolicyPath;
        if (!File.Exists(path)) return (true, "there was no policy to clear");
        try { File.Delete(path); }
        catch (Exception ex) { return (false, "could not delete " + path + ": " + ex.Message); }
        EvidenceChain.Append("policy", null, new JsonObject { ["action"] = "clear", ["file"] = path });
        return (true, "policy cleared — the agent surface follows the gate and its leases again");
    }

    // ---------------------------------------------------------------- checks

    public static string Describe(AgentRuleSet rules)
        => "verbs " + (rules.Verbs.Count == 0 ? "any" : string.Join(",", rules.Verbs))
           + " · hosts " + (rules.Hosts.Count == 0 ? "any" : string.Join(",", rules.Hosts))
           + " · paths " + (rules.AllowPaths.Count == 0 ? "any" : string.Join(",", rules.AllowPaths))
           + (rules.DenyPaths.Count == 0 ? "" : " except " + string.Join(",", rules.DenyPaths))
           + " · destructive " + rules.Destructive
           + " · approvers " + (rules.Approvers.Count == 0 ? "none" : string.Join(",", rules.Approvers.Select(a => a.Name)));

    /// <summary>Does this call pass the policy? Null rules mean there is no policy to pass.</summary>
    public static bool Check(AgentRuleSet? rules, Session session, string? verb, string? command,
        out string refusal, out string? tokenId, string? tokenStore = null)
    {
        refusal = "";
        tokenId = null;
        if (rules is null || verb is null) return true;

        if (rules.Verbs.Count > 0 && !rules.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            refusal = "the signed policy allows only " + string.Join(", ", rules.Verbs) + ", so '" + verb
                      + "' is refused. Change it: SSH-it.exe --policy-set <file> (or --policy-clear --confirm).";
            return false;
        }

        if (rules.Hosts.Count > 0 && !rules.Hosts.Any(pattern => HostKeys.HostPatternMatches(pattern, session.Host, session.Port)))
        {
            refusal = "the signed policy allows hosts " + string.Join(", ", rules.Hosts) + "; '"
                      + session.DisplayTarget + "' is not one of them.";
            return false;
        }

        if (command is not null && PathVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            var denied = rules.DenyPaths.FirstOrDefault(p => PathMatches(p, command));
            if (denied is not null)
            {
                refusal = "the signed policy denies paths matching '" + denied + "', and '" + command + "' matches it.";
                return false;
            }
            if (rules.AllowPaths.Count > 0 && !rules.AllowPaths.Any(p => PathMatches(p, command)))
            {
                refusal = "the signed policy allows only paths " + string.Join(", ", rules.AllowPaths)
                          + "; '" + command + "' is not one of them.";
                return false;
            }
        }

        if (command is not null && IsCommandVerb(verb) && IsDestructive(command) && rules.Destructive != "allow")
        {
            if (rules.Destructive == "deny")
            {
                refusal = "the signed policy refuses destructive commands outright (destructive: deny).";
                return false;
            }

            var tokens = AgentTokens.Load(tokenStore);
            if (!AgentTokens.ValidFor(tokens.Rows, rules, session, command, out var token, out var why))
            {
                refusal = "the signed policy requires a two-person token for a destructive command: "
                          + explain(why) + " Request one: SSH-it.exe --policy-token " + session.Id + " \"" + command
                          + "\" --minutes 10 — a second key must then countersign it: SSH-it.exe --policy-approve <id>.";
                return false;
            }
            tokenId = token!.Id;
        }

        return true;

        static string explain(string why) => why switch
        {
            "none" => "no token covers this command.",
            "used" => "the token for this command was already used.",
            "expired" => "the token for this command expired.",
            "unsigned" => "the token for this command carries no second signature.",
            "foreign" => "the token was countersigned by a key that is not a registered approver.",
            _ => "no token covers this command."
        };
    }

    private static bool IsCommandVerb(string verb)
        => verb.Equals("exec", StringComparison.OrdinalIgnoreCase) || verb.Equals("watch", StringComparison.OrdinalIgnoreCase);

    public static bool IsDestructive(string command)
        => BlastRadius.Assess(command).Any(f => f.Severity == "high");

    /// <summary>Glob over a remote path: `**` crosses separators, `*` and `?` do not.</summary>
    public static bool PathMatches(string pattern, string path)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*') { regex.Append(".*"); i++; }
                else regex.Append("[^/]*");
            }
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }
        regex.Append('$');
        try { return Regex.IsMatch(path, regex.ToString(), RegexOptions.CultureInvariant); }
        catch { return false; }
    }

    /// <summary>Non-throwing form, for filters that must skip rather than fail (the fleet scan).</summary>
    public static bool Allows(Session session, string? verb, string? command, out string refusal)
    {
        var loaded = Load();
        if (!loaded.Ok) { refusal = loaded.Error!; return false; }
        return Check(loaded.Rules, session, verb, command, out refusal, out _);
    }

    /// <summary>Enforces the policy, records the refusal, and throws the message the caller prints.</summary>
    public static void Require(Session session, string? verb, string? command)
    {
        var loaded = Load();
        if (!loaded.Ok)
            throw new AgentAccessDeniedException("the signed policy failed verification (" + loaded.Error
                                                + "). Agent access stays closed until it is repaired.");

        if (!Check(loaded.Rules, session, verb, command, out var refusal, out var tokenId))
        {
            EvidenceChain.Append("policy-deny", session, new JsonObject
            {
                ["verb"] = verb ?? "",
                ["command"] = command ?? "",
                ["reason"] = refusal.Length > 300 ? refusal.Substring(0, 300) : refusal
            });
            throw new AgentAccessDeniedException(refusal);
        }

        if (tokenId is not null) AgentTokens.Consume(tokenId, session, command);
    }
}

/// <summary>One countersignable token for one destructive command.</summary>
public sealed class AgentToken
{
    public string Id { get; set; } = "";
    public string Session { get; set; } = "";
    public string Command { get; set; } = "";
    public string Nonce { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expires { get; set; }
    public string RequesterPub { get; set; } = "";
    public string RequesterSig { get; set; } = "";
    public List<AgentTokenApproval> Approvals { get; set; } = new();
    public string Used { get; set; } = "";

    public bool Verified { get; set; }

    public JsonObject Payload() => new()
    {
        ["id"] = Id,
        ["session"] = Session,
        ["command"] = Command,
        ["nonce"] = Nonce,
        ["created"] = Created.ToUniversalTime().ToString("O"),
        ["expires"] = Expires.ToUniversalTime().ToString("O")
    };
}

public sealed class AgentTokenApproval
{
    public string Name { get; set; } = "";
    public string Pub { get; set; } = "";
    public string Sig { get; set; } = "";
}

/// <summary>
/// R3-07's second half: a token for one destructive command, requested by this device and countersigned
/// by a key registered in the policy. Single use, expiring, and bound to the exact command string.
/// </summary>
public static class AgentTokens
{
    public static string DirectoryPath => Path.Combine(AgentPolicyFile.DirectoryPath, "tokens");

    public static string StorePath => Path.Combine(DirectoryPath, "tokens.json");

    public static string ApproversPath => Path.Combine(AgentPolicyFile.DirectoryPath, "approvers");

    public sealed class Loaded
    {
        public List<AgentToken> Rows { get; } = new();
        public string? Error { get; set; }
    }

    public static Loaded Load(string? path = null)
    {
        path ??= StorePath;
        var result = new Loaded();
        if (!File.Exists(path)) return result;

        JsonObject document;
        try { document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (Exception ex) { result.Error = "the token store is unreadable: " + ex.Message; return result; }

        if (document["rows"] is not JsonArray rows) return result;

        var deviceKey = EvidenceChain.DevicePublicKey();
        foreach (var entry in rows.OfType<JsonObject>())
        {
            var token = new AgentToken
            {
                Id = entry["id"]?.GetValue<string>() ?? "",
                Session = entry["session"]?.GetValue<string>() ?? "",
                Command = entry["command"]?.GetValue<string>() ?? "",
                Nonce = entry["nonce"]?.GetValue<string>() ?? "",
                Created = ParseTime(entry["created"]),
                Expires = ParseTime(entry["expires"]),
                RequesterPub = entry["requesterPub"]?.GetValue<string>() ?? "",
                RequesterSig = entry["requesterSig"]?.GetValue<string>() ?? "",
                Used = entry["used"]?.GetValue<string>() ?? ""
            };
            if (entry["approvals"] is JsonArray approvals)
                foreach (var a in approvals.OfType<JsonObject>())
                    token.Approvals.Add(new AgentTokenApproval
                    {
                        Name = a["name"]?.GetValue<string>() ?? "",
                        Pub = a["pub"]?.GetValue<string>() ?? "",
                        Sig = a["sig"]?.GetValue<string>() ?? ""
                    });

            token.Verified = deviceKey.Length > 0
                             && token.RequesterPub == deviceKey
                             && EvidenceChain.VerifyCanonical(deviceKey, token.RequesterSig,
                                 EvidenceChain.CanonicalJson(token.Payload()));
            result.Rows.Add(token);
        }

        return result;
    }

    private static DateTimeOffset ParseTime(JsonNode? node)
    {
        try
        {
            return DateTimeOffset.Parse(node?.GetValue<string>() ?? "", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }
        catch { return DateTimeOffset.MinValue; }
    }

    private static void Save(List<AgentToken> rows, string? path = null)
    {
        path ??= StorePath;
        var array = new JsonArray();
        foreach (var token in rows)
            array.Add(new JsonObject
            {
                ["id"] = token.Id,
                ["session"] = token.Session,
                ["command"] = token.Command,
                ["nonce"] = token.Nonce,
                ["created"] = token.Created.ToUniversalTime().ToString("O"),
                ["expires"] = token.Expires.ToUniversalTime().ToString("O"),
                ["requesterPub"] = token.RequesterPub,
                ["requesterSig"] = token.RequesterSig,
                ["approvals"] = new JsonArray(token.Approvals.Select(a => (JsonNode)new JsonObject
                {
                    ["name"] = a.Name,
                    ["pub"] = a.Pub,
                    ["sig"] = a.Sig
                }).ToArray()),
                ["used"] = token.Used
            });

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary,
            new JsonObject { ["version"] = 1, ["rows"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Requests a token for one exact command. Signed by this device's key.</summary>
    public static AgentToken Create(Session session, string command, int minutes, string? path = null)
    {
        if (minutes < 1 || minutes > AgentLease.MaxMinutes)
            throw new InvalidOperationException($"--minutes must be 1..{AgentLease.MaxMinutes}");
        command = command.Trim();
        if (command.Length == 0) throw new InvalidOperationException("--policy-token needs the exact command");

        var deviceKey = EvidenceChain.DevicePublicKey();
        if (deviceKey.Length == 0) throw new InvalidOperationException("the device key is unavailable");

        var now = DateTimeOffset.UtcNow;
        var token = new AgentToken
        {
            Id = Guid.NewGuid().ToString("n").Substring(0, 10),
            Session = session.Id,
            Command = command,
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
            Created = now,
            Expires = now.AddMinutes(minutes),
            RequesterPub = deviceKey
        };
        token.RequesterSig = EvidenceChain.Sign(EvidenceChain.CanonicalJson(token.Payload()))!.Value.Signature;

        var store = Load(path);
        store.Rows.Add(token);
        Save(store.Rows, path);
        EvidenceChain.Append("policy", session, new JsonObject
        {
            ["action"] = "token-request",
            ["id"] = token.Id,
            ["command"] = token.Command,
            ["minutes"] = minutes,
            ["expires"] = token.Expires.ToString("O")
        });
        return token;
    }

    /// <summary>
    /// Countersigns a token with a registered approver key. The key must differ from the device key:
    /// a token signed twice by the requesting device is not a second person.
    /// </summary>
    public static (bool Ok, string Detail) Approve(string id, string approverName, string? path = null,
        string? policyPath = null, string? approversDir = null)
    {
        var policy = AgentPolicyFile.Load(policyPath);
        if (!policy.Ok) return (false, policy.Error!);
        if (policy.Rules is null) return (false, "there is no signed policy, so no approver is registered");

        var approver = policy.Rules.Approvers.FirstOrDefault(a => a.Name == approverName);
        if (approver is null)
            return (false, "no approver named '" + approverName + "' in the policy — register one: SSH-it.exe --policy-approver " + approverName);

        var deviceKey = EvidenceChain.DevicePublicKey();
        if (string.Equals(approver.Pub, deviceKey, StringComparison.Ordinal))
            return (false, "approver '" + approverName + "' uses this device's own key — a second signature has to come from a different key");

        var key = LoadApproverKey(approverName, approversDir);
        if (key is null) return (false, "the private key for approver '" + approverName + "' is missing or unreadable on this machine");

        var store = Load(path);
        var token = store.Rows.FirstOrDefault(t => t.Id == id);
        if (token is null) return (false, "no token with id '" + id + "'");
        if (!token.Verified) return (false, "token " + id + " does not verify against this device's key");
        if (token.Used.Length > 0) return (false, "token " + id + " was already used at " + token.Used);
        if (token.Expires <= DateTimeOffset.UtcNow) return (false, "token " + id + " expired at " + token.Expires.ToLocalTime().ToString("HH:mm"));

        using (key)
        {
            token.Approvals.RemoveAll(a => a.Name == approverName);
            token.Approvals.Add(new AgentTokenApproval
            {
                Name = approverName,
                Pub = approver.Pub,
                Sig = Convert.ToBase64String(key.SignData(
                    Encoding.UTF8.GetBytes(EvidenceChain.CanonicalJson(token.Payload())), HashAlgorithmName.SHA256))
            });
        }

        Save(store.Rows, path);
        EvidenceChain.Append("policy", null, new JsonObject
        {
            ["action"] = "token-approve",
            ["id"] = token.Id,
            ["approver"] = approverName,
            ["command"] = token.Command
        });
        return (true, "token " + id + " countersigned by '" + approverName + "'");
    }

    /// <summary>Creates an approver key on this machine, protects it with DPAPI and registers its public half.</summary>
    public static (bool Ok, string Detail) NewApprover(string name, string? policyPath = null, string? approversDir = null)
    {
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9_-]{0,31}$"))
            return (false, "approver names are [a-z0-9_-], 1..32 chars");

        var policy = AgentPolicyFile.Load(policyPath);
        if (!policy.Ok) return (false, policy.Error!);

        var rules = policy.Rules ?? new AgentRuleSet { Updated = DateTimeOffset.UtcNow };
        if (rules.Approvers.Any(a => a.Name == name))
            return (false, "an approver named '" + name + "' already exists (its public key is in the policy)");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var guarded = EvidenceChain.ProtectBytes(key.ExportPkcs8PrivateKey(), "SSH-it approver key");
        if (guarded is null)
            return (false, "DPAPI refused to protect the approver key, so it was not stored");

        var directory = approversDir ?? ApproversPath;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".key"), Convert.ToBase64String(guarded), new UTF8Encoding(false));

        rules.Approvers.Add(new AgentApprover { Name = name, Pub = pub });
        rules.Updated = DateTimeOffset.UtcNow;
        AgentPolicyFile.Save(rules, policyPath);

        EvidenceChain.Append("policy", null, new JsonObject
        {
            ["action"] = "approver-add",
            ["name"] = name,
            ["pub"] = Fingerprint(pub)
        });
        return (true, "approver '" + name + "' registered: " + Fingerprint(pub)
                      + " (private key DPAPI-protected at " + Path.Combine(directory, name + ".key") + ")");
    }

    private static ECDsa? LoadApproverKey(string name, string? approversDir = null)
    {
        try
        {
            var file = Path.Combine(approversDir ?? ApproversPath, name + ".key");
            if (!File.Exists(file)) return null;
            var guarded = Convert.FromBase64String(File.ReadAllText(file).Trim());
            var plain = EvidenceChain.UnprotectBytes(guarded);
            if (plain is null) return null;
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(plain, out _);
            return key;
        }
        catch { return null; }
    }

    private static string Fingerprint(string pub)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(pub))).TrimEnd('=');

    /// <summary>
    /// Is there a usable token for this call? A token authorises when it is this session's, this exact
    /// command, unexpired, unused, signed by this device, and countersigned by a registered approver
    /// whose key is not the device key.
    /// </summary>
    public static bool ValidFor(List<AgentToken> rows, AgentRuleSet rules, Session session, string command,
        out AgentToken? token, out string why)
    {
        token = null;
        why = "none";
        var now = DateTimeOffset.UtcNow;
        var deviceKey = EvidenceChain.DevicePublicKey();
        var approverKeys = rules.Approvers.ToDictionary(a => a.Pub, a => a.Name, StringComparer.Ordinal);

        var candidates = rows.Where(t => t.Session == session.Id
                                         && string.Equals(t.Command.Trim(), command.Trim(), StringComparison.Ordinal))
                             .OrderByDescending(t => t.Created)
                             .ToList();
        if (candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            if (!candidate.Verified) { why = "foreign"; continue; }
            if (candidate.Used.Length > 0) { why = "used"; continue; }
            if (candidate.Expires <= now) { why = "expired"; continue; }

            var second = candidate.Approvals.FirstOrDefault(a =>
                a.Pub != deviceKey && approverKeys.ContainsKey(a.Pub)
                && EvidenceChain.VerifyCanonical(a.Pub, a.Sig, EvidenceChain.CanonicalJson(candidate.Payload())));
            if (second is null)
            {
                why = candidate.Approvals.Any(a => a.Pub != deviceKey) ? "foreign" : "unsigned";
                continue;
            }

            if (approverKeys.TryGetValue(second.Pub, out var registered) && registered != second.Name)
            {
                why = "foreign";
                continue;
            }

            token = candidate;
            why = "";
            return true;
        }

        return false;
    }

    /// <summary>Marks a token used. The chain keeps the record; the flag stops the obvious replay.</summary>
    public static void Consume(string id, Session? session, string? command, string? path = null)
    {
        var store = Load(path);
        var token = store.Rows.FirstOrDefault(t => t.Id == id);
        if (token is null) return;
        token.Used = DateTimeOffset.UtcNow.ToString("O");
        Save(store.Rows, path);
        EvidenceChain.Append("policy-token", session, new JsonObject
        {
            ["id"] = token.Id,
            ["command"] = command ?? token.Command,
            ["approvals"] = new JsonArray(token.Approvals.Select(a => (JsonNode)JsonValue.Create(a.Name)!).ToArray())
        });
    }

    /// <summary>Drops the token store. Used rows otherwise accumulate for the life of the profile.</summary>
    public static (bool Ok, string Detail) Clear(string? path = null)
    {
        path ??= StorePath;
        var store = Load(path);
        if (!File.Exists(path)) return (true, "there were no tokens to clear");
        try { File.Delete(path); }
        catch (Exception ex) { return (false, "could not delete " + path + ": " + ex.Message); }
        EvidenceChain.Append("policy", null, new JsonObject
        {
            ["action"] = "tokens-clear",
            ["rows"] = store.Rows.Count
        });
        return (true, "cleared " + store.Rows.Count + " token(s); requests and consumption stay in the evidence chain");
    }

    public static string ListText(bool json, string? path = null)
    {
        var store = Load(path);
        var now = DateTimeOffset.UtcNow;
        if (json)
            return JsonSerializer.Serialize(new
            {
                ok = store.Error is null,
                error = store.Error,
                rows = store.Rows.Select(t => new
                {
                    id = t.Id, session = t.Session, command = t.Command,
                    created = t.Created.ToString("O"), expires = t.Expires.ToString("O"),
                    verified = t.Verified, used = t.Used,
                    approvals = t.Approvals.Select(a => new { name = a.Name, pub = Fingerprint(a.Pub) }),
                    usable = t.Verified && t.Used.Length == 0 && t.Expires > now && t.Approvals.Count > 0
                })
            });

        if (store.Rows.Count == 0) return "no tokens";
        return string.Join("\n", store.Rows.OrderByDescending(t => t.Created).Select(t =>
            t.Id + "  " + t.Session + "  \"" + t.Command + "\"  "
            + (t.Used.Length > 0 ? "used " + t.Used.Substring(0, 19).Replace('T', ' ')
                : t.Expires <= now ? "expired"
                : t.Approvals.Count == 0 ? "waiting for a second signature"
                : "ready") + "  approvals: "
            + (t.Approvals.Count == 0 ? "none" : string.Join(",", t.Approvals.Select(a => a.Name)))));
    }
}
