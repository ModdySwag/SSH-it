using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// One signed grant on the agent surface: a <c>lease</c> (a set of verbs for one session) or an
/// <c>approval</c> (one exact command). Both expire; both are signed by the device key.
/// </summary>
public sealed class AgentGrant
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "lease";     // lease | approval
    public string SessionId { get; set; } = "";
    public string Target { get; set; } = "";
    public List<string> Verbs { get; set; } = new();
    public string Command { get; set; } = "";
    public DateTimeOffset Granted { get; set; }
    public DateTimeOffset Expires { get; set; }

    /// <summary>The pty this approval is bound to (0 = session-bound), and when it started.</summary>
    public int Pane { get; set; }
    public string PaneStart { get; set; } = "";

    /// <summary>Whether that pty is still the same live process — computed when the store is read.</summary>
    public bool PaneAlive { get; set; }

    public bool PaneBound => Pane > 0 && PaneStart.Length > 0;

    /// <summary>False when the row's signature did not verify against this machine's device key.</summary>
    public bool Verified { get; set; }

    public bool Active => DateTimeOffset.UtcNow < Expires;

    /// <summary>Active, verified, and — when bound to a pty — still the pane it was approved in.</summary>
    public bool Authorising => Active && Verified && (!PaneBound || PaneAlive);

    public TimeSpan Remaining => Expires - DateTimeOffset.UtcNow;

    /// <summary>
    /// Does this grant authorise one call? An expired row, an unverified row, a closed pty and a
    /// different command all say no.
    /// </summary>
    public bool Covers(string verb, string? command)
    {
        if (!Active || !Verified) return false;
        if (!Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) return false;
        if (Kind != "approval") return true;
        if (PaneBound && !PaneAlive) return false;
        return string.Equals(Command.Trim(), (command ?? "").Trim(), StringComparison.Ordinal);
    }

    public string Scope => Kind == "approval"
        ? "exec \"" + Command + "\""
        : string.Join(", ", Verbs);

    public string Standing(DateTimeOffset now)
    {
        if (!Verified) return "SIGNATURE BAD";
        var left = Expires - now;
        if (left <= TimeSpan.Zero) return "expired " + Ago(now - Expires) + " ago";
        if (PaneBound && !PaneAlive) return "pane closed (pid " + Pane + ")";
        return "active, " + Ago(left) + " left" + (PaneBound ? ", pane pid " + Pane : "");
    }

    private static string Ago(TimeSpan span)
    {
        if (span.TotalSeconds < 90) return Math.Max(1, (int)span.TotalSeconds) + "s";
        if (span.TotalMinutes < 90) return (int)span.TotalMinutes + "m";
        return (int)span.TotalHours + "h" + (span.Minutes > 0 ? span.Minutes + "m" : "");
    }

    /// <summary>The fields the signature covers, in the shape both sides serialize.</summary>
    public JsonObject Signed()
    {
        var verbs = new JsonArray();
        foreach (var verb in Verbs) verbs.Add(verb);
        return new JsonObject
        {
            ["id"] = Id,
            ["kind"] = Kind,
            ["session"] = SessionId,
            ["target"] = Target,
            ["verbs"] = verbs,
            ["command"] = Command,
            ["granted"] = Granted.ToUniversalTime().ToString("O"),
            ["expires"] = Expires.ToUniversalTime().ToString("O"),
            ["pane"] = Pane,
            ["paneStart"] = PaneStart
        };
    }
}

/// <summary>
/// R3-06: the observe-only agent lease. Granting a lease marks a session as <b>managed</b>: from
/// then on every agent verb on that session must be covered by an active, correctly signed lease or
/// approval, and a call outside it is refused with the exact command that would authorise it.
/// Revoking the last grant returns the session to the default agent policy.
///
/// Honest limits, because this is a security-shaped feature: the store is a file this user can
/// write, and so is <c>SSH-it.exe</c> — a process running as the same user can revoke, delete or
/// re-sign it. The lease constrains an agent that is obeying the surface and leaves a signed trail
/// when it does not; it is not a sandbox. Every grant, revoke and refusal is appended to the
/// evidence chain, so a removed row is visible there even though the lease file itself has no
/// hash chain.
/// </summary>
public static class AgentLease
{
    /// <summary>Verbs a lease can name. Anything else is a typo, not a grant.</summary>
    public static readonly string[] KnownVerbs =
    {
        "exec", "sftp-list", "sftp-get", "sftp-put", "sftp-mirror", "tunnel-up",
        "watch", "diff", "blast", "snapshot", "posture"
    };

    /// <summary>Verbs an approval can name: the two that run a command string on the host.</summary>
    public static readonly string[] ApprovableVerbs = { "exec", "watch" };

    public const int MaxMinutes = 1440;

    public static string DirectoryPath => Path.Combine(DataStore.RootPath, "leases");

    public static string StorePath => Path.Combine(DirectoryPath, "leases.json");

    public sealed class Store
    {
        public List<AgentGrant> Rows { get; } = new();
        public string? Error { get; set; }
        public bool Ok => Error is null;
    }

    // ---------------------------------------------------------------- reading

    public static Store Load(string? path = null)
    {
        path ??= StorePath;
        var store = new Store();
        if (!File.Exists(path)) return store;

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            store.Error = "cannot read " + path + ": " + ex.Message;
            return store;
        }

        JsonObject? document;
        try { document = JsonNode.Parse(text) as JsonObject; }
        catch (Exception ex)
        {
            store.Error = "the lease file is not JSON: " + ex.Message;
            return store;
        }

        if (document?["rows"] is not JsonArray rows)
        {
            store.Error = "the lease file has no rows array";
            return store;
        }

        var deviceKey = EvidenceChain.DevicePublicKey();
        if (deviceKey.Length == 0 && rows.Count > 0)
        {
            store.Error = "the device key is unavailable, so no lease can be verified";
            return store;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row)
            {
                store.Error = "row " + (i + 1) + " is not an object";
                return store;
            }

            var grant = new AgentGrant
            {
                Id = row["id"]?.GetValue<string>() ?? "",
                Kind = row["kind"]?.GetValue<string>() ?? "lease",
                SessionId = row["session"]?.GetValue<string>() ?? "",
                Target = row["target"]?.GetValue<string>() ?? "",
                Command = row["command"]?.GetValue<string>() ?? "",
                Granted = ParseTime(row["granted"]),
                Expires = ParseTime(row["expires"]),
                Pane = row["pane"] is JsonNode pane && int.TryParse(pane.ToString(), out var pid) ? pid : 0,
                PaneStart = row["paneStart"]?.GetValue<string>() ?? ""
            };
            if (row["verbs"] is JsonArray verbs)
                foreach (var verb in verbs)
                    if (verb?.GetValue<string>() is { Length: > 0 } name) grant.Verbs.Add(name);

            grant.PaneAlive = !grant.PaneBound || PaneIsAlive(grant.Pane, grant.PaneStart);

            var signature = row["sig"]?.GetValue<string>() ?? "";
            grant.Verified = signature.Length > 0
                             && EvidenceChain.VerifyCanonical(deviceKey, signature,
                                 EvidenceChain.CanonicalJson(grant.Signed()));

            /* A granted time in the future is a clock change, not a grant: refuse the row. Signed
               state should never be able to extend itself by moving the clock forward. */
            var plausible = grant.Granted <= DateTimeOffset.UtcNow.AddMinutes(5)
                            && grant.Expires > grant.Granted
                            && grant.Id.Length > 0 && grant.SessionId.Length > 0;

            if (!grant.Verified || !plausible)
            {
                store.Error = "row " + (i + 1) + " (" + (grant.Id.Length > 0 ? grant.Id : "no id") + ") "
                              + (!grant.Verified ? "does not verify against the device key" : "is malformed");
                store.Rows.Add(grant);
                return store;
            }

            store.Rows.Add(grant);
        }

        return store;
    }

    private static DateTimeOffset ParseTime(JsonNode? node)
    {
        try { return DateTimeOffset.Parse(node?.GetValue<string>() ?? "", null,
            System.Globalization.DateTimeStyles.RoundtripKind); }
        catch { return DateTimeOffset.MinValue; }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes the rows back, signing each. Rows are re-signed on every write because the whole file
    /// is one object: a revoke has to rewrite its neighbours too.
    /// </summary>
    private static void Save(List<AgentGrant> rows, string path)
    {
        var array = new JsonArray();
        foreach (var grant in rows)
        {
            var signed = grant.Signed();
            var signature = EvidenceChain.Sign(EvidenceChain.CanonicalJson(signed))
                            ?? throw new InvalidOperationException(
                                "the device key is unavailable, so a lease cannot be signed (DPAPI refused)");
            var row = signed.DeepClone() as JsonObject ?? new JsonObject();
            row["alg"] = EvidenceChain.Algorithm;
            row["sig"] = signature.Signature;
            array.Add(row);
        }

        var document = new JsonObject { ["version"] = 1, ["rows"] = array };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Grant verbs on one session. <paramref name="command"/> makes it an approval instead: one exact
    /// command, one verb (exec or watch), for the same window.
    /// </summary>
    public static AgentGrant Grant(Session session, IEnumerable<string> verbs, int minutes,
        string? command = null, string? path = null, (int Pid, DateTime Started)? pane = null)
    {
        path ??= StorePath;
        if (minutes < 1 || minutes > MaxMinutes)
            throw new InvalidOperationException($"--minutes must be 1..{MaxMinutes} (a lease should expire)");

        var list = verbs.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).Distinct().ToList();
        var approval = command is not null;
        if (approval)
        {
            if (command!.Trim().Length == 0) throw new InvalidOperationException("an approval needs a command");
            var verb = list.FirstOrDefault() ?? "exec";
            if (!ApprovableVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("an approval covers " + string.Join("/", ApprovableVerbs)
                                                    + ", not '" + verb + "'");
            list = new List<string> { verb };
        }

        if (list.Count == 0) throw new InvalidOperationException("a lease needs at least one verb (--verbs exec,sftp-list)");
        var unknown = list.Where(v => !KnownVerbs.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("unknown verb(s): " + string.Join(", ", unknown)
                                                + " — known: " + string.Join(", ", KnownVerbs));

        var store = Load(path);
        if (store.Error is not null)
            throw new InvalidOperationException("the lease store is broken, refusing to add to it: " + store.Error);

        var now = DateTimeOffset.UtcNow;
        var grant = new AgentGrant
        {
            Id = Guid.NewGuid().ToString("n").Substring(0, 10),
            Kind = approval ? "approval" : "lease",
            SessionId = session.Id,
            Target = session.DisplayTarget,
            Verbs = list,
            Command = approval ? command!.Trim() : "",
            Granted = now,
            Expires = now.AddMinutes(minutes),
            Verified = true
        };

        if (approval && pane is { Pid: > 0 } bound)
        {
            grant.Pane = bound.Pid;
            grant.PaneStart = bound.Started.ToUniversalTime().ToString("O");
            grant.PaneAlive = PaneIsAlive(grant.Pane, grant.PaneStart);
            if (!grant.PaneAlive)
                throw new InvalidOperationException("the pty (pid " + bound.Pid + ") is not running, so an approval "
                                                    + "cannot be bound to it");
        }

        store.Rows.Add(grant);
        Save(store.Rows, path);
        EvidenceChain.Append("lease", session, new JsonObject
        {
            ["action"] = "grant",
            ["id"] = grant.Id,
            ["kind"] = grant.Kind,
            ["verbs"] = new JsonArray(list.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            ["command"] = grant.Command,
            ["minutes"] = minutes,
            ["expires"] = grant.Expires.ToString("O"),
            ["pane"] = grant.Pane,
            ["paneStart"] = grant.PaneStart
        });
        return grant;
    }

    /// <summary>Removes a grant. Revoking the last one returns the session to the default policy.</summary>
    public static bool Revoke(string id, out string error, string? path = null)
    {
        path ??= StorePath;
        error = "";
        var store = Load(path);
        if (store.Error is not null)
        {
            /* Rewriting a store whose rows did not verify would silently re-sign the tampered ones.
               The only honest repair for a broken store is to drop it whole. */
            error = "the lease store does not verify (" + store.Error + ") — run --lease-clear to drop it and "
                    + "return every session to the default policy.";
            return false;
        }

        var match = store.Rows.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            error = store.Rows.Count == 0
                ? "no leases are stored"
                : "no lease has id '" + id + "' — stored: " + string.Join(", ", store.Rows.Select(g => g.Id));
            return false;
        }

        store.Rows.Remove(match);
        Save(store.Rows, path);
        EvidenceChain.Append("lease", null, new JsonObject
        {
            ["action"] = "revoke",
            ["id"] = match.Id,
            ["kind"] = match.Kind,
            ["session"] = match.SessionId,
            ["target"] = match.Target
        });
        return true;
    }

    /// <summary>
    /// Drops the whole store. The escape hatch for a lease file that no longer verifies: nothing in it
    /// can be trusted, so nothing is kept, and the sessions named there return to the default policy.
    /// </summary>
    public static (bool Ok, string Detail) Clear(string? path = null)
    {
        path ??= StorePath;
        var store = Load(path);
        var detail = store.Rows.Count == 0 && store.Error is null
            ? "there were no leases to clear"
            : "cleared " + store.Rows.Count + " lease row(s)" + (store.Error is null ? "" : " (the store was broken: " + store.Error + ")");

        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (Exception ex) { return (false, "could not delete " + path + ": " + ex.Message); }
        }

        EvidenceChain.Append("lease", null, new JsonObject { ["action"] = "clear", ["detail"] = detail });
        return (true, detail + "; agent access on those sessions follows the default policy again");
    }

    /// <summary>
    /// Is the pty an approval was bound to still the same live process? Pid alone is not enough — a
    /// recycled pid would inherit an approval — so the start time has to match too (the same test
    /// TunnelSupervisor uses to re-attach to a tunnel).
    /// </summary>
    public static bool PaneIsAlive(int pid, string startedIso)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (!DateTimeOffset.TryParse(startedIso, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var started)) return false;
            return Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- enforcement

    /// <summary>
    /// May this call proceed? A session no grant names is unmanaged and keeps the old behaviour; a
    /// managed session needs an active grant that covers the verb (and, for an approval, the command).
    /// </summary>
    public static bool Allows(Session session, string? verb, string? command, out string refusal, string? path = null)
    {
        refusal = "";
        if (verb is null) return true;                    // local-only metadata (e.g. --snapshots listing)

        var store = Load(path);
        if (store.Error is not null)
        {
            refusal = "the lease store failed verification (" + store.Error + "). Agent access stays closed "
                      + "until it is repaired: run SSH-it.exe --lease-verify, then --lease-clear if the store "
                      + "is broken (that returns every session to the default policy and is recorded).";
            return false;
        }

        var mine = store.Rows.Where(r => string.Equals(r.SessionId, session.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return true;                 // unmanaged: the agent gate decides, not the lease

        if (mine.Any(r => r.Covers(verb, command))) return true;

        refusal = Refusal(session, verb, command, mine);
        return false;
    }

    /// <summary>
    /// The sessions a fleet-wide read may touch. A leased session that does not name the verb is
    /// skipped, not probed, and the caller reports the skip: a silently shorter fleet reads as a
    /// smaller fleet.
    /// </summary>
    public static List<Session> Leasable(IEnumerable<Session> sessions, out List<string> skipped, string? path = null)
    {
        var allowed = new List<Session>();
        skipped = new List<string>();
        foreach (var session in sessions)
        {
            if (Allows(session, "posture", null, out _, path)) allowed.Add(session);
            else skipped.Add(session.Id);
        }
        return allowed;
    }

    /// <summary>The refusal text: what is authorising, why this call is outside it, and the exact fix.</summary>
    public static string Refusal(Session session, string verb, string? command, List<AgentGrant> mine)
    {
        var now = DateTimeOffset.UtcNow;
        var authorising = mine.Where(g => g.Authorising).ToList();
        var closed = mine.Where(g => g.Kind == "approval" && g.Active && g.Verified && g.PaneBound && !g.PaneAlive
                                     && g.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)).ToList();
        var expired = mine.Where(g => !g.Active && g.Verified && g.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)).ToList();

        var text = new StringBuilder();
        text.Append("agent access to '").Append(session.DisplayTarget).Append("' is leased; '").Append(verb)
            .Append("' is not authorised for this call. ");

        if (authorising.Count > 0)
        {
            text.Append("Authorising now: ")
                .Append(string.Join("; ", authorising.Select(g => g.Id + " (" + g.Scope + ", " + g.Standing(now) + ")")))
                .Append(". ");
        }
        else
        {
            var last = mine.OrderByDescending(g => g.Expires).First();
            text.Append("Nothing is authorising: ").Append(last.Id).Append(" (").Append(last.Scope).Append(") — ")
                .Append(last.Standing(now)).Append(". ");
        }

        if (closed.Count > 0)
        {
            text.Append(closed.Count == 1 ? "Approval " : "Approvals ")
                .Append(string.Join(", ", closed.Select(g => g.Id + " (pid " + g.Pane + ")")))
                .Append(closed.Count == 1 ? " was" : " were")
                .Append(closed.Count == 1
                    ? " bound to the pane it was approved in, and that pane has closed. "
                    : " bound to the panes they were approved in, and those panes have closed. ");
        }

        if (command is not null)
        {
            var near = authorising.FirstOrDefault(g => g.Kind == "approval"
                                                       && g.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)
                                                       && !string.Equals(g.Command.Trim(), command.Trim(), StringComparison.Ordinal));
            if (near is not null)
                text.Append("Approval ").Append(near.Id).Append(" covers exec of \"").Append(near.Command)
                    .Append("\", not \"").Append(command).Append("\". ");
        }

        if (expired.Count > 0)
        {
            text.Append("The expired grant ")
                .Append(string.Join(", ", expired.Select(g => g.Id + " (" + g.Scope + ", ended "
                    + g.Expires.ToLocalTime().ToString("HH:mm:ss") + ")")))
                .Append(" covered that. ");
        }

        text.Append("To authorise it, run: SSH-it.exe --lease-grant ").Append(session.Id)
            .Append(" --verbs ").Append(verb).Append(" --minutes 15");
        if (ApprovableVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase) && command is not null)
            text.Append(" — or, narrower, approve this one command: SSH-it.exe --approve ")
                .Append(session.Id).Append(" \"").Append(command).Append("\" --minutes 5");
        text.Append('.');
        return text.ToString();
    }

    /// <summary>Enforces the lease, records the refusal, and throws the message the caller prints.</summary>
    public static void Require(Session session, string? verb, string? command, string? path = null)
    {
        if (Allows(session, verb, command, out var refusal, path)) return;

        try
        {
            var store = Load(path);
            var ids = store.Rows.Where(r => r.SessionId == session.Id).Select(r => r.Id).ToList();
            EvidenceChain.Append("lease-deny", session, new JsonObject
            {
                ["verb"] = verb ?? "",
                ["command"] = command ?? "",
                ["reason"] = refusal.Length > 300 ? refusal.Substring(0, 300) : refusal,
                ["grants"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
                ["storeError"] = store.Error ?? ""
            });
        }
        catch (Exception ex)
        {
            Log.Warn("lease: could not record the refusal: " + ex.Message);
        }

        throw new AgentAccessDeniedException(refusal);
    }

    // ---------------------------------------------------------------- reporting

    public static string ListText(bool json, string? path = null)
    {
        var store = Load(path);
        var now = DateTimeOffset.UtcNow;
        if (json)
        {
            return JsonSerializer.Serialize(new
            {
                ok = store.Ok,
                error = store.Error,
                store = path ?? StorePath,
                rows = store.Rows.Select(g => new
                {
                    id = g.Id, kind = g.Kind, session = g.SessionId, target = g.Target,
                    verbs = g.Verbs, command = g.Command,
                    granted = g.Granted.ToString("O"),
                    expires = g.Expires.ToString("O"),
                    active = g.Authorising, verified = g.Verified,
                    pane = g.Pane, paneStart = g.PaneStart, paneAlive = g.PaneAlive,
                    secondsLeft = Math.Max(0, (int)g.Remaining.TotalSeconds)
                })
            });
        }

        if (store.Error is not null) return "lease store BROKEN — " + store.Error + "\nstore: " + (path ?? StorePath);
        if (store.Rows.Count == 0)
            return "no leases — every session uses the default agent policy (Settings > agent gate).\nstore: " + (path ?? StorePath);

        var lines = new List<string>
        {
            store.Rows.Count + " grant(s), " + store.Rows.Count(g => g.Authorising) + " authorising"
        };
        foreach (var grant in store.Rows.OrderByDescending(g => g.Expires))
            lines.Add($"{grant.Id}  {grant.Kind,-8} {grant.Target,-28} {grant.Scope,-24} {grant.Standing(now)}");
        lines.Add("session: " + string.Join(", ", store.Rows.Select(g => g.SessionId).Distinct()));
        lines.Add("store: " + (path ?? StorePath));
        return string.Join("\n", lines);
    }

    /// <summary>Signature-only verdict, for --lease-verify and the selftest.</summary>
    public static (bool Ok, string Detail) Verify(string? path = null)
    {
        var store = Load(path);
        if (store.Error is not null) return (false, store.Error);
        if (store.Rows.Count == 0) return (true, "no leases stored");
        return (true, store.Rows.Count + " lease(s) verify against the device key "
                       + EvidenceChain.KeyFingerprint().Replace("SHA256:", ""));
    }
}
