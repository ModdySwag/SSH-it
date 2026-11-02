using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// The agent-facing surface: one-shot commands over the same sessions, keys, jump hosts
/// and credential vault the GUI uses, plus a stdio MCP server that wraps exactly those
/// verbs. Nothing here opens a window, so it is safe to run from any shell or agent.
/// </summary>
public static class AgentCli
{
    private static readonly string[] Verbs =
    {
        "--exec", "--sftp-list", "--sftp-get", "--sftp-put", "--sftp-mirror", "--sessions", "--mcp",
        "--tunnel-up", "--tunnel-list", "--tunnel-down", "--posture",
        "--evidence-verify", "--evidence-export", "--watch", "--diff", "--blast",
        "--snapshot", "--snapshots",
        "--lease-grant", "--lease-list", "--lease-revoke", "--lease-verify", "--lease-clear", "--approve",
        "--policy-show", "--policy-set", "--policy-verify", "--policy-clear", "--policy-approver",
        "--policy-token", "--policy-approve", "--policy-tokens-clear", "--cast-index",
        "--service-add", "--service-remove", "--service-list", "--service-up", "--service-check",
        "--inventory-validate", "--inventory-export", "--inventory-import",
        "--queue-add", "--queue-list", "--queue-run", "--queue-clear",
        "--ledger", "--ledger-clear", "--workspace-list", "--workspace-forget",
        "--sync-export", "--sync-status", "--sync-import", "--deploy", "--audit-export", "--bench"
    };

    public static bool Wants(string[] args) => args.Any(a => Verbs.Contains(a, StringComparer.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        DetachStandardHandles();

        // The WPF dispatcher installs a SynchronizationContext. Blocking on an await
        // while it is current deadlocks, so agent verbs run without one.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            // A --json caller must be able to parse stdout on EVERY path, including a refusal that
            // was written to stderr by an early return. Buffer both streams for --json only, and if
            // the verb failed with an empty stdout, turn its stderr sentence into the documented
            // {"ok":false,"error":…} shape. Text mode keeps streaming straight through.
            if (!args.Contains("--json", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--mcp", StringComparer.OrdinalIgnoreCase)) return RunCore(args);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var realOut = Console.Out;
            var realError = Console.Error;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                var code = RunCore(args);
                var text = stdout.ToString();
                var errors = stderr.ToString();
                if (code != 0 && string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(errors))
                    text = JsonSerializer.Serialize(new { ok = false, error = errors.Trim() }) + Environment.NewLine;
                realOut.Write(text);
                realError.Write(errors);
                return code;
            }
            finally
            {
                Console.SetOut(realOut);
                Console.SetError(realError);
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    /// <summary>
    /// A detached child inherits every inheritable handle, including the caller's stdout pipe.
    /// That keeps the pipe open after this process exits, so anything capturing our output (a
    /// shell, an agent, an MCP client) waits for an EOF that never comes — the command looks
    /// hung even though the tunnel is up. Mark our own standard handles non-inheritable first;
    /// the child's redirected pipes are created by .NET separately and still reach it.
    /// </summary>
    private static void DetachStandardHandles()
    {
        const uint inheritFlag = 1;
        try
        {
            foreach (var id in new[] { -10, -11, -12 })   // stdin, stdout, stderr
            {
                var handle = GetStdHandle(id);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
                SetHandleInformation(handle, inheritFlag, 0);
            }
        }
        catch { /* best effort: the verbs still work, a caller may just wait longer */ }
    }

    /// <summary>A one-line confirmation a --json caller can parse instead of prose.</summary>
    private static void Note(bool json, bool ok, string detail)
        => Console.Out.WriteLine(json ? JsonSerializer.Serialize(new { ok, detail }) : detail);

    private static int RunCore(string[] args)
    {
        // A caller that asked for JSON gets JSON on every path, including a refusal: a machine
        // parsing stdout should never have to fall back to prose on stderr.
        var jsonWanted = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase)) return McpServer.Run();

            var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

            if (args.Contains("--bench", StringComparer.OrdinalIgnoreCase))
            {
                var report = Bench.Run(OptionalValueAfter(args, "--compare"));
                Console.Out.Write(json
                    ? report.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                    : report.Text);
                return 0;
            }

            var timeout = 60;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out var seconds) && seconds > 0)
                    timeout = seconds;
            }

            var store = new DataStore();
            store.Load();

            if (args.Contains("--sessions", StringComparer.OrdinalIgnoreCase))
            {
                EmitSessions(store, json);
                return 0;
            }

            if (args.Contains("--ledger", StringComparer.OrdinalIgnoreCase))
            {
                // C9: the query is every word after --ledger up to the next flag.
                var words = new List<string>();
                var at = Array.FindIndex(args, a => a.Equals("--ledger", StringComparison.OrdinalIgnoreCase));
                for (var i = at + 1; i < args.Length; i++)
                {
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) break;
                    words.Add(args[i]);
                }

                var query = string.Join(" ", words);
                var limit = int.TryParse(OptionalValueAfter(args, "--limit"), out var want) ? Math.Clamp(want, 1, 500) : 20;
                DateTime? since = null;
                var sinceText = OptionalValueAfter(args, "--since");
                if (!string.IsNullOrWhiteSpace(sinceText))
                {
                    // Same rule as --audit-export: a bad date is refused, never "no filter", or a typo
                    // quietly prints the whole ledger as if it were the filtered answer.
                    if (!DateTime.TryParse(sinceText, out var sinceValue))
                    {
                        Console.Error.WriteLine("--since is not a date: '" + sinceText + "' (try 2026-10-01)");
                        return 2;
                    }
                    since = sinceValue;
                }

                var rows = CommandLedger.Search(query, limit, null,
                    OptionalValueAfter(args, "--host"), OptionalValueAfter(args, "--session"),
                    OptionalValueAfter(args, "--exit"), since);
                Console.Out.Write(CommandLedger.Format(rows, json));
                return query.Length > 0 && rows.Count == 0 ? 1 : 0;
            }

            if (args.Contains("--ledger-clear", StringComparer.OrdinalIgnoreCase))
            {
                if (!args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("--ledger-clear deletes the local command ledger.");
                    Console.Error.WriteLine("Re-run with --confirm to do it.");
                    return 2;
                }
                var removed = CommandLedger.Clear();
                Note(json, true, "ledger cleared — " + removed + " row(s) removed.");
                return 0;
            }

            if (args.Contains("--workspace-list", StringComparer.OrdinalIgnoreCase))
            {
                Console.Out.Write(WorkspaceStore.ListText(json));
                return 0;
            }

            var auditPath = OptionalValueAfter(args, "--audit-export");
            if (args.Contains("--audit-export", StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(auditPath))
                {
                    Console.Error.WriteLine("--audit-export needs a file path, e.g. audit-2026-11.zip");
                    return 1;
                }
                DateTime? since = null;
                var sinceText = OptionalValueAfter(args, "--since");
                if (!string.IsNullOrWhiteSpace(sinceText))
                {
                    // A bad date used to fall through to "no filter" and quietly exported everything.
                    if (!DateTime.TryParse(sinceText, out var sinceValue))
                    {
                        Console.Error.WriteLine("--since is not a date: '" + sinceText + "' (try 2026-10-01)");
                        return 2;
                    }
                    since = sinceValue.Date;
                }
                var limit = 5000;
                if (int.TryParse(OptionalValueAfter(args, "--limit"), out var limitValue) && limitValue > 0)
                    limit = limitValue;
                var version = typeof(AgentCli).Assembly.GetName().Version?.ToString() ?? "1.0";
                // Ledger output is opt-in: it is where `cat .env` and `env` leave their secrets.
                var withOutput = args.Contains("--with-output", StringComparer.OrdinalIgnoreCase);
                var bundle = AuditExport.Export(store.Sessions, auditPath!, since, limit, version, withOutput);
                Console.Out.WriteLine(json ? bundle.ToJson() : bundle.ToText());
                // An archive that carries a broken chain is evidence of a problem, not a clean audit:
                // a script that branches on the exit code must see it.
                return bundle.Ok ? 0 : 1;
            }

            // L1 safe deploy: staged write, validate over the session's own ssh, then move into place
            // with the previous file kept. It resolves as a put, because that is the authority it needs.
            var deploy = ValueAfter(args, "--deploy");
            if (deploy is not null)
            {
                var parts = SplitRest(deploy.Value.Body, 2);
                var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
                var customValidator = OptionalValueAfter(args, "--validator");

                // Validation runs a command over ssh, so a deploy that validates needs the exec authority
                // too — otherwise --validator is an exec call wearing a put's permissions. The gate must
                // see the string that will RUN (the substituted validator), not the remote path: a
                // policy/lease check against the path let `--validator "rm -rf /srv"` through unexamined.
                var template = string.IsNullOrWhiteSpace(customValidator)
                    ? SafeDeploy.ValidatorFor(parts[1])
                    : customValidator;
                var willValidate = !string.IsNullOrWhiteSpace(template);
                var validatorCommand = willValidate
                    ? SafeDeploy.ValidatorCommand(template!, SafeDeploy.StagedPath(parts[1]))
                    : null;
                var session = Resolve(store, deploy.Value.Key, willValidate ? "exec" : "sftp-put",
                                      willValidate ? validatorCommand : parts[1]);
                var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
                var result = RunDeploy(session, parts[0], parts[1], customValidator, force, timeout, dryRun);
                Console.Out.WriteLine(json ? result.ToJson() : result.ToText());
                return result.Ok ? 0 : 1;
            }

            // R3-25: settings sync through the user's own git. Export writes a folder the user commits and
            // pushes; import on the other machine merges it. Secrets are never in these files.
            if (args.Contains("--sync-export", StringComparer.OrdinalIgnoreCase))
            {
                var folder = OptionalValueAfter(args, "--sync-export");
                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.Error.WriteLine("--sync-export needs a folder to write into.");
                    return 1;
                }
                var target = Path.GetFullPath(folder);
                var version = typeof(AgentCli).Assembly.GetName().Version?.ToString() ?? "1.0";
                SettingsSync.Export(store, target, version);
                var files = Directory.GetFiles(target).Length;
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, folder = target, files })
                    : "Exported to " + target + " (" + files + " file(s))." + Environment.NewLine
                      + "Commit and push that folder; `--sync-import <folder>` on the other machine merges it.");
                return 0;
            }

            if (args.Contains("--sync-status", StringComparer.OrdinalIgnoreCase))
            {
                var folder = OptionalValueAfter(args, "--sync-status");
                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.Error.WriteLine("--sync-status needs a folder.");
                    return 1;
                }
                var resolved = Path.GetFullPath(folder);
                if (!Directory.Exists(resolved))
                {
                    Console.Error.WriteLine("no such sync folder: " + resolved);
                    return 1;
                }
                var report = SettingsSync.Status(store, resolved);
                Console.Out.Write(json ? report.ToJson() + Environment.NewLine : report.Describe());
                return 0;
            }

            if (args.Contains("--sync-import", StringComparer.OrdinalIgnoreCase))
            {
                var folder = OptionalValueAfter(args, "--sync-import");
                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.Error.WriteLine("--sync-import needs a folder.");
                    return 1;
                }
                var importPath = Path.GetFullPath(folder);
                if (!Directory.Exists(importPath))
                {
                    Console.Error.WriteLine("no such sync folder: " + importPath);
                    return 1;
                }
                var overwrite = args.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
                var withSettings = args.Contains("--with-settings", StringComparer.OrdinalIgnoreCase);
                var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
                var report = SettingsSync.Import(store, importPath, overwrite, withSettings, dryRun);
                Console.Out.Write(json ? report.ToJson() + Environment.NewLine : report.Describe());
                if (report.Error is { Length: > 0 }) return 1;
                // Exit 1 when conflicts were reported: the import happened, but this machine kept its own
                // copy of those items and a script needs to know that without parsing the text.
                var conflicts = report.Sessions.Conflicting + report.Folders.Conflicting + report.Snippets.Conflicting;
                return conflicts > 0 ? 1 : 0;
            }

            var forget = OptionalValueAfter(args, "--workspace-forget");
            if (forget is not null)
            {
                if (!WorkspaceStore.Remove(forget))
                {
                    Console.Error.WriteLine("no workspace named '" + forget + "'");
                    return 1;
                }
                Note(json, true, "workspace \"" + forget + "\" removed.");
                return 0;
            }

            // R3-06: the lease verbs are owner actions, so they resolve a session WITHOUT the lease
            // check (a grant that had to be granted would be a deadlock), and they never open a host.
            if (args.Contains("--lease-list", StringComparer.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine(AgentLease.ListText(json));
                return AgentLease.Load().Ok ? 0 : 1;
            }

            if (args.Contains("--lease-verify", StringComparer.OrdinalIgnoreCase))
            {
                var verdict = AgentLease.Verify();
                Note(json, verdict.Ok, (verdict.Ok ? "lease store ok — " : "lease store BROKEN — ") + verdict.Detail);
                return verdict.Ok ? 0 : 1;
            }

            if (args.Contains("--lease-clear", StringComparer.OrdinalIgnoreCase))
            {
                if (!args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("--lease-clear drops every lease and returns those sessions to the default "
                                            + "agent policy. It is recorded in the evidence chain. Re-run with --confirm.");
                    return 2;
                }
                var cleared = AgentLease.Clear();
                Note(json, cleared.Ok, cleared.Detail);
                return cleared.Ok ? 0 : 1;
            }

            var revokeId = OptionalValueAfter(args, "--lease-revoke");
            if (revokeId is not null)
            {
                if (!AgentLease.Revoke(revokeId, out var revokeError))
                {
                    Console.Error.WriteLine(revokeError);
                    return 2;
                }
                Note(json, true, "lease " + revokeId + " revoked"
                                      + " — if that was the last grant on the session, it follows the default agent policy again");
                return 0;
            }

            var grantVerb = ValueAfter(args, "--lease-grant");
            if (grantVerb is not null)
            {
                var session = Resolve(store, grantVerb.Value.Key, null, null);
                var verbs = (OptionalValueAfter(args, "--verbs") ?? "sftp-list,posture,blast")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var minutes = MinutesAfter(args, "--minutes", 15);
                var grant = AgentLease.Grant(session, verbs, minutes);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new
                    {
                        ok = true, id = grant.Id, kind = grant.Kind, session = grant.SessionId,
                        target = grant.Target, verbs = grant.Verbs,
                        granted = grant.Granted.ToString("O"), expires = grant.Expires.ToString("O")
                    })
                    : "lease " + grant.Id + " on '" + grant.Target + "': " + string.Join(", ", grant.Verbs)
                      + " for " + minutes + " min (until " + grant.Expires.ToLocalTime().ToString("HH:mm") + ").\n"
                      + "Every agent verb on this session now needs a covering grant; `--lease-revoke " + grant.Id
                      + "` ends it.");
                return 0;
            }

            var approveVerb = ValueAfter(args, "--approve");
            if (approveVerb is not null)
            {
                var session = Resolve(store, approveVerb.Value.Key, null, null);
                var command = approveVerb.Value.Body.Trim();
                if (command.Length == 0)
                {
                    Console.Error.WriteLine("usage: --approve <session> \"<exact command>\" [--verb exec|watch] [--minutes N]");
                    return 2;
                }
                var verb = OptionalValueAfter(args, "--verb") ?? "exec";
                var minutes = MinutesAfter(args, "--minutes", 5);
                var approval = AgentLease.Grant(session, new[] { verb }, minutes, command);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new
                    {
                        ok = true, id = approval.Id, kind = approval.Kind, session = approval.SessionId,
                        target = approval.Target, verb, command = approval.Command,
                        expires = approval.Expires.ToString("O")
                    })
                    : "approval " + approval.Id + " on '" + approval.Target + "': " + verb + " of \"" + approval.Command
                      + "\" until " + approval.Expires.ToLocalTime().ToString("HH:mm")
                      + ".\nIt authorises that exact command and nothing else.");
                return 0;
            }

            // R3-07: the signed policy and its two-person tokens. Owner actions, like the leases.
            if (args.Contains("--policy-show", StringComparer.OrdinalIgnoreCase))
            {
                var loaded = AgentPolicyFile.Load();
                if (json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new
                    {
                        ok = loaded.Ok,
                        error = loaded.Error,
                        file = AgentPolicyFile.PolicyPath,
                        policy = loaded.Rules is null ? null : new
                        {
                            verbs = loaded.Rules.Verbs,
                            hosts = loaded.Rules.Hosts,
                            allowPaths = loaded.Rules.AllowPaths,
                            denyPaths = loaded.Rules.DenyPaths,
                            destructive = loaded.Rules.Destructive,
                            approvers = loaded.Rules.Approvers.Select(a => new { name = a.Name, pub = a.Pub }),
                            updated = loaded.Rules.Updated.ToString("O"),
                            note = loaded.Rules.Note
                        },
                        // One stdout line is one JSON document: a caller that asked for JSON must not
                        // have to skip a trailing sentence of token prose to parse this.
                        tokens = JsonNode.Parse(AgentTokens.ListText(true))
                    }));
                }
                else
                {
                    Console.Out.WriteLine(loaded.Ok
                        ? (loaded.Rules is null ? "no policy — the agent surface follows the gate and its leases" : "policy: " + AgentPolicyFile.Describe(loaded.Rules))
                        : "policy BROKEN — " + loaded.Error);
                    Console.Out.WriteLine("file: " + AgentPolicyFile.PolicyPath);
                    Console.Out.WriteLine(AgentTokens.ListText(false));
                }
                return loaded.Ok ? 0 : 1;
            }

            var policySet = OptionalValueAfter(args, "--policy-set");
            if (policySet is not null)
            {
                var note = OptionalValueAfter(args, "--note");
                var rules = AgentPolicyFile.Set(policySet, note);
                Note(json, true, "policy set and signed: " + AgentPolicyFile.Describe(rules));
                return 0;
            }

            if (args.Contains("--policy-verify", StringComparer.OrdinalIgnoreCase))
            {
                var verdict = AgentPolicyFile.Verify();
                Note(json, verdict.Ok, (verdict.Ok ? "policy ok — " : "policy BROKEN — ") + verdict.Detail);
                return verdict.Ok ? 0 : 1;
            }

            if (args.Contains("--policy-clear", StringComparer.OrdinalIgnoreCase))
            {
                if (!args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("--policy-clear removes the signed policy, so the surface falls back to the "
                                            + "gate and its leases. Re-run with --confirm.");
                    return 2;
                }
                var cleared = AgentPolicyFile.Clear();
                Note(json, cleared.Ok, cleared.Detail);
                return cleared.Ok ? 0 : 1;
            }

            if (args.Contains("--policy-tokens-clear", StringComparer.OrdinalIgnoreCase))
            {
                if (!args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("--policy-tokens-clear drops every token, used ones included. Re-run with --confirm.");
                    return 2;
                }
                var clearedTokens = AgentTokens.Clear();
                Note(json, clearedTokens.Ok, clearedTokens.Detail);
                return clearedTokens.Ok ? 0 : 1;
            }

            var approverName = OptionalValueAfter(args, "--policy-approver");
            if (approverName is not null)
            {
                var added = AgentTokens.NewApprover(approverName);
                Note(json, added.Ok, added.Detail);
                return added.Ok ? 0 : 1;
            }

            var tokenRequest = ValueAfter(args, "--policy-token");
            if (tokenRequest is not null)
            {
                var session = Resolve(store, tokenRequest.Value.Key, null, null);
                var command = tokenRequest.Value.Body.Trim();
                if (command.Length == 0)
                {
                    Console.Error.WriteLine("usage: --policy-token <session> \"<exact command>\" [--minutes N]");
                    return 2;
                }
                var minutes = MinutesAfter(args, "--minutes", 10);
                var token = AgentTokens.Create(session, command, minutes);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, id = token.Id, command = token.Command, expires = token.Expires.ToString("O") })
                    : "token " + token.Id + " for \"" + token.Command + "\" until " + token.Expires.ToLocalTime().ToString("HH:mm")
                      + ".\nIt authorises nothing until a registered approver countersigns it: SSH-it.exe --policy-approve "
                      + token.Id + " <approver>" + " (register one with --policy-approver <name>).");
                return 0;
            }

            var tokenApprove = ValueAfter(args, "--policy-approve");
            if (tokenApprove is not null)
            {
                var id = tokenApprove.Value.Key;
                var who = tokenApprove.Value.Body.Trim();
                if (who.Length == 0)
                {
                    Console.Error.WriteLine("usage: --policy-approve <token id> <approver>");
                    return 2;
                }
                var approved = AgentTokens.Approve(id, who);
                Note(json, approved.Ok, approved.Detail);
                return approved.Ok ? 0 : 1;
            }

            var castIndex = OptionalValueAfter(args, "--cast-index");
            if (castIndex is not null)
            {
                var blocks = CastIndex.Load(castIndex);
                Console.Out.WriteLine(json ? CastIndex.ToJson(castIndex, blocks) : CastIndex.ToText(castIndex, blocks));
                return blocks.Count > 0 ? 0 : 1;
            }

            // R3-12: named service tunnels — one name, one DSN, one health check.
            if (args.Contains("--service-list", StringComparer.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine(ServiceTunnels.ListText(json));
                return 0;
            }

            var serviceAdd = ValueAfter(args, "--service-add");
            if (serviceAdd is not null)
            {
                var parts = serviceAdd.Value.Body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    Console.Error.WriteLine("usage: --service-add <name> <session> <L:bind:port:host:port> "
                                            + "[--dsn <template>] [--check \"<command>\"] [--note text]");
                    return 2;
                }
                var session = Resolve(store, parts[0], null, null);
                var service = ServiceTunnels.Add(serviceAdd.Value.Key, session, string.Join(" ", parts.Skip(1)),
                    OptionalValueAfter(args, "--dsn"), OptionalValueAfter(args, "--check"),
                    OptionalValueAfter(args, "--note"));
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, name = service.Name, session = service.SessionId, spec = service.Spec, dsn = service.Dsn })
                    : "service " + service.Name + " added: " + service.Spec
                      + (service.CheckCommand.Length > 0 ? " (check: " + service.CheckCommand + ")" : ""));
                return 0;
            }

            var serviceRemove = OptionalValueAfter(args, "--service-remove");
            if (serviceRemove is not null)
            {
                if (!ServiceTunnels.Remove(serviceRemove, out var removeError))
                {
                    Console.Error.WriteLine(removeError);
                    return 2;
                }
                Note(json, true, "service " + serviceRemove + " removed (a tunnel it started keeps running)");
                return 0;
            }

            var serviceUp = OptionalValueAfter(args, "--service-up");
            if (serviceUp is not null)
            {
                var service = ServiceTunnels.Find(serviceUp)
                              ?? throw new InvalidOperationException("no service named '" + serviceUp + "' — run --service-list");
                var session = Resolve(store, service.SessionId, "tunnel-up", service.Spec);
                var report = ServiceTunnels.Up(service, session);
                Console.Out.WriteLine(json ? ServiceTunnels.ReportJson(report) : ServiceTunnels.ReportText(report));
                return report.Status == "down" ? 1 : 0;
            }

            var serviceCheck = OptionalValueAfter(args, "--service-check");
            if (serviceCheck is not null)
            {
                var service = ServiceTunnels.Find(serviceCheck)
                              ?? throw new InvalidOperationException("no service named '" + serviceCheck + "' — run --service-list");
                var verb = service.CheckCommand.Length > 0 ? "exec" : "tunnel-up";
                var session = Resolve(store, service.SessionId, verb, service.CheckCommand.Length > 0 ? service.CheckCommand : service.Spec);
                var report = ServiceTunnels.Check(service, session);
                Console.Out.WriteLine(json ? ServiceTunnels.ReportJson(report) : ServiceTunnels.ReportText(report));
                return report.Status == "down" ? 1 : 0;
            }

            // R3-21: the git-friendly host inventory.
            var inventoryValidate = OptionalValueAfter(args, "--inventory-validate");
            if (inventoryValidate is not null)
            {
                var issues = new List<InventoryIssue>();
                var document = Inventory.Parse(Inventory.ReadFile(inventoryValidate), issues);
                Inventory.Validate(document, issues);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new
                    {
                        ok = !Inventory.HasErrors(issues),
                        hosts = document.Hosts.Count,
                        errors = issues.Count(i => i.Severity == "error"),
                        warnings = issues.Count(i => i.Severity == "warning"),
                        issues = issues.Select(i => new { severity = i.Severity, line = i.Line, message = i.Message })
                    })
                    : Inventory.IssueText(issues, document.Hosts.Count));
                return Inventory.HasErrors(issues) ? 1 : 0;
            }

            if (args.Contains("--inventory-export", StringComparer.OrdinalIgnoreCase))
            {
                var yaml = Inventory.ToYaml(store.Sessions);
                var target = OptionalValueAfter(args, "--inventory-export");
                if (target is null)
                {
                    Console.Out.WriteLine(json
                        ? new JsonObject
                        {
                            ["ok"] = true, ["format"] = "yaml",
                            ["hosts"] = store.Sessions.Count(s => !s.IsLocal), ["data"] = yaml
                        }.ToJsonString()
                        : yaml);
                }
                else
                {
                    File.WriteAllText(target, yaml, new UTF8Encoding(false));
                    Console.Out.WriteLine(json
                        ? JsonSerializer.Serialize(new { ok = true, file = target, hosts = store.Sessions.Count(s => !s.IsLocal) })
                        : "exported " + store.Sessions.Count(s => !s.IsLocal) + " host(s) to " + target);
                }
                return 0;
            }

            var inventoryImport = OptionalValueAfter(args, "--inventory-import");
            if (inventoryImport is not null)
            {
                var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
                var issues = new List<InventoryIssue>();
                var document = Inventory.Parse(Inventory.ReadFile(inventoryImport), issues);
                Inventory.Validate(document, issues);
                if (Inventory.HasErrors(issues))
                {
                    Console.Out.WriteLine(json
                        ? JsonSerializer.Serialize(new
                        {
                            ok = false,
                            error = "import refused: fix the errors first (--inventory-validate " + inventoryImport + ")",
                            hosts = document.Hosts.Count,
                            issues = issues.Select(i => new { severity = i.Severity, line = i.Line, message = i.Message })
                        })
                        : Inventory.IssueText(issues, document.Hosts.Count));
                    Console.Error.WriteLine("import refused: fix the errors first (--inventory-validate " + inventoryImport + ")");
                    return 1;
                }

                var plans = Inventory.Plan(store.Sessions, document);
                if (!dryRun)
                {
                    Inventory.Apply(store.Sessions, document);
                    store.SaveSessions();
                }

                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new
                    {
                        ok = true, dryRun, added = plans.Count(p => p.Action == "add"),
                        updated = plans.Count(p => p.Action == "update"), unchanged = plans.Count(p => p.Action == "same"),
                        plans = plans.Select(p => new { action = p.Action, name = p.Name, changes = p.Changes })
                    })
                    : Inventory.PlanText(plans) + (dryRun ? "\n(dry run — nothing written)" : "\napplied to sessions.json"));
                return 0;
            }

            // R3-22: the transfer queue.
            var queueAdd = ValueAfter(args, "--queue-add");
            if (queueAdd is not null)
            {
                var parts = queueAdd.Value.Body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    Console.Error.WriteLine("usage: --queue-add <session> get <remote> <local>  |  put <local> <remote>");
                    return 2;
                }
                var session = Resolve(store, queueAdd.Value.Key, null, null);
                // `put <local> <remote>` reads like the sftp verbs; the job stores remote/local explicitly.
                var isPut = string.Equals(parts[0], "put", StringComparison.OrdinalIgnoreCase);
                var remote = isPut ? parts[2] : parts[1];
                var local = isPut ? parts[1] : parts[2];
                var job = TransferQueue.Add(session, parts[0], remote, local);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, id = job.Id, kind = job.Kind, remote = job.Remote, local = job.Local })
                    : "queued " + job.Id + ": " + job.Kind + " " + job.Remote + " ↔ " + job.Local);
                return 0;
            }

            if (args.Contains("--queue-list", StringComparer.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine(TransferQueue.ListText(json));
                return 0;
            }

            if (args.Contains("--queue-clear", StringComparer.OrdinalIgnoreCase))
            {
                var cleared = TransferQueue.Clear();
                Note(json, true, cleared + " finished job(s) removed from the queue");
                return 0;
            }

            var queueRun = OptionalValueAfter(args, "--queue-run");
            if (queueRun is not null || args.Contains("--queue-run", StringComparer.OrdinalIgnoreCase))
            {
                var only = queueRun;
                var jobs = TransferQueue.Load();
                if (only is not null)
                {
                    var keep = Resolve(store, only, null, null).Id;
                    jobs = jobs.Where(j => j.SessionId == keep).ToList();
                }
                var failedJobs = TransferQueue.Run(job => Resolve(store, job.SessionId,
                        job.Kind == "get" ? "sftp-get" : "sftp-put", job.Remote),
                    jobs, null, null, store.Settings.SftpResume);
                var done = jobs.Count(j => j.State == "done");
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ran = jobs.Count, done, failed = failedJobs, jobs })
                    : done + " done, " + failedJobs + " failed\n" + string.Join("\n", jobs.Select(j => j.Describe())));
                return failedJobs == 0 ? 0 : 1;
            }

            if (args.Contains("--posture", StringComparer.OrdinalIgnoreCase))
            {
                var key = OptionalValueAfter(args, "--posture");
                var skipped = new List<string>();
                var chosen = key is null
                    ? ScanTargets(store, out skipped)
                    : new List<Session> { Resolve(store, key, "posture") };
                if (skipped.Count > 0)
                    Console.Error.WriteLine("skipped " + skipped.Count + " session(s) a lease or the policy does not allow posture on: "
                                            + string.Join(", ", skipped));

                // A fleet scan is several handshakes; the per-host cap is 45 s, so the default
                // wait is generous and --timeout still overrides it.
                var postureTimeout = args.Contains("--timeout", StringComparer.OrdinalIgnoreCase) ? timeout : 600;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(postureTimeout));
                var hosts = FleetPosture.ScanAsync(chosen, null, null, cts.Token).GetAwaiter().GetResult();
                Console.Out.WriteLine(json ? FleetPosture.ToJson(hosts) : FleetPosture.ToText(hosts));
                return 0;
            }

            if (args.Contains("--evidence-verify", StringComparer.OrdinalIgnoreCase))
            {
                var verdict = EvidenceChain.Verify();
                Console.Out.WriteLine(json ? EvidenceJson(verdict) : EvidenceText(verdict));
                return verdict.Ok ? 0 : 1;
            }

            var export = ValueAfter(args, "--evidence-export");
            if (export is not null)
            {
                Note(json, true, EvidenceChain.Export(export.Value.Key));
                return 0;
            }

            var watchVerb = ValueAfter(args, "--watch");
            if (watchVerb is not null)
            {
                var command = watchVerb.Value.Body.Trim();
                if (command.Length == 0) throw new InvalidOperationException("--watch needs a command after the session");
                var session = Resolve(store, watchVerb.Value.Key, "watch", command);

                var seconds = 10;
                var runs = 3;
                for (var i = 0; i < args.Length - 1; i++)
                {
                    if (args[i].Equals("--seconds", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(args[i + 1], out var parsedSeconds)) seconds = parsedSeconds;
                    if (args[i].Equals("--runs", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(args[i + 1], out var parsedRuns)) runs = parsedRuns;
                }

                var watch = new CommandWatch(session, command, seconds);
                using var done = new ManualResetEventSlim();
                watch.Ran += run => { if (run.Index >= Math.Max(1, runs)) done.Set(); };
                watch.Start();
                done.Wait(TimeSpan.FromSeconds(Math.Max(20, seconds * (Math.Max(1, runs) + 2))));
                watch.Stop();
                Console.Out.WriteLine(json ? WatchJson(watch) : watch.Receipt());
                return watch.Runs.Count > 0 ? 0 : 1;
            }

            var diffVerb = ValueAfter(args, "--diff");
            if (diffVerb is not null)
            {
                var command = diffVerb.Value.Body.Trim();
                if (command.Length == 0) throw new InvalidOperationException("--diff needs a command after the session");
                var first = Resolve(store, diffVerb.Value.Key, "diff", command);
                var against = OptionalValueAfter(args, "--against");
                var second = against is null ? first : Resolve(store, against, "diff", command);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, timeout)));
                var left = BlockDiff.RunAsync(first, command, cts.Token).GetAwaiter().GetResult();
                var right = BlockDiff.RunAsync(second, command, cts.Token).GetAwaiter().GetResult();
                var rows = BlockDiff.Align(left.Output, right.Output);
                Console.Out.WriteLine(json
                    ? DiffJson(first, second, left, right, rows)
                    : BlockDiff.ToText(first.DisplayTarget, second.DisplayTarget, rows)
                      + "\n" + BlockDiff.Verdict(left, right, rows));
                return left.Exit == 0 && right.Exit == 0 ? 0 : 1;
            }

            var blastVerb = ValueAfter(args, "--blast");
            if (blastVerb is not null)
            {
                var command = blastVerb.Value.Body.Trim();
                if (command.Length == 0) throw new InvalidOperationException("--blast needs a command after the session");
                var session = Resolve(store, blastVerb.Value.Key, "blast", command);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, timeout)));
                var report = BlastRadius.PreviewAsync(session, command, cts.Token).GetAwaiter().GetResult();
                Console.Out.WriteLine(json ? BlastJson(report) : BlastText(report));
                return 0;
            }

            if (args.Contains("--snapshots", StringComparer.OrdinalIgnoreCase))
            {
                var which = OptionalValueAfter(args, "--snapshots");
                var sessionId = which is null ? null : Resolve(store, which, null).Id;
                var points = UndoStore.List(sessionId);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(points.Select(p => new
                    {
                        id = p.Id, at = p.At, target = p.Target, path = p.Path,
                        snapshot = p.Snapshot, kind = p.Kind, restore = p.Restore
                    }))
                    : points.Count == 0 ? "no snapshots recorded" : string.Join("\n", points.Select(UndoStore.Describe)));
                return 0;
            }

            var snapshotVerb = ValueAfter(args, "--snapshot");
            if (snapshotVerb is not null)
            {
                var pattern = snapshotVerb.Value.Body.Trim();
                if (pattern.Length == 0) throw new InvalidOperationException("--snapshot needs a path after the session");
                var session = Resolve(store, snapshotVerb.Value.Key, "snapshot", pattern);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, timeout)));
                var (point, error) = UndoStore.SnapshotAsync(session, pattern, cts.Token).GetAwaiter().GetResult();
                if (point is null)
                {
                    Console.Error.WriteLine("no snapshot: " + error);
                    return 1;
                }
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new
                    {
                        ok = true, id = point.Id, path = point.Path, snapshot = point.Snapshot,
                        kind = point.Kind, restore = point.Restore
                    })
                    : UndoStore.Describe(point));
                return 0;
            }

            var exec = ValueAfter(args, "--exec");
            if (exec is not null)
            {
                // "--exec host --json 'cmd'" used to hand the flag in as the command and report exit 0.
                if (string.IsNullOrWhiteSpace(exec.Value.Body) || exec.Value.Body.TrimStart().StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("--exec needs a command after the session: --exec <session> \"<command>\" [--json]");
                    return 1;
                }
                var session = Resolve(store, exec.Value.Key, "exec", exec.Value.Body);
                var result = RunCommand(session, exec.Value.Body, timeout);
                Console.Out.WriteLine(json ? result.ToJson() : result.ToText());
                return result.Ok ? 0 : 1;
            }

            var list = ValueAfter(args, "--sftp-list");
            if (list is not null)
            {
                var session = Resolve(store, list.Value.Key, "sftp-list", list.Value.Body);
                var entries = RunSftpList(session, list.Value.Body).GetAwaiter().GetResult();
                Console.Out.WriteLine(json ? SftpJson(entries) : SftpText(entries));
                return 0;
            }

            var resume = args.Any(a => string.Equals(a, "--resume", StringComparison.OrdinalIgnoreCase));

            var get = ValueAfter(args, "--sftp-get");
            if (get is not null)
            {
                var parts = SplitRest(get.Value.Body, 2);
                var session = Resolve(store, get.Value.Key, "sftp-get", parts[0]);
                var bytes = RunSftpGet(session, parts[0], parts[1], resume).GetAwaiter().GetResult();
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, remote = parts[0], local = parts[1], bytes, resumed = resume })
                    : $"downloaded {parts[0]} -> {parts[1]} ({bytes} bytes{(resume ? ", resumed" : "")})");
                return 0;
            }

            var put = ValueAfter(args, "--sftp-put");
            if (put is not null)
            {
                var parts = SplitRest(put.Value.Body, 2);
                var session = Resolve(store, put.Value.Key, "sftp-put", parts[1]);
                var bytes = RunSftpPut(session, parts[0], parts[1], resume).GetAwaiter().GetResult();
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, local = parts[0], remote = parts[1], bytes, resumed = resume })
                    : $"uploaded {parts[0]} -> {parts[1]} ({bytes} bytes{(resume ? ", resumed" : "")})");
                return 0;
            }

            var mirror = ValueAfter(args, "--sftp-mirror");
            if (mirror is not null)
            {
                var parts = SplitRest(mirror.Value.Body, 2);
                var session = Resolve(store, mirror.Value.Key, "sftp-mirror", parts[0]);
                var dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
                var items = RunSftpMirror(session, parts[0], parts[1], dryRun).GetAwaiter().GetResult();
                var downloads = items.Count(i => i.Action == "download");
                var skipped = items.Count(i => i.Action == "skip");
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = true, remote = parts[0], local = parts[1], dryRun, downloads, skipped, items })
                    : $"mirror {parts[0]} -> {parts[1]}: {downloads} to download, {skipped} unchanged{(dryRun ? " (dry run)" : "")}");
                return 0;
            }

            var tunnelUp = ValueAfter(args, "--tunnel-up");
            if (tunnelUp is not null)
            {
                var session = Resolve(store, tunnelUp.Value.Key, "tunnel-up", tunnelUp.Value.Body);
                try
                {
                    var entry = TunnelSupervisor.Start(session, tunnelUp.Value.Body);
                    var spec = TunnelSupervisor.Parse(entry.Spec);
                    Console.Out.WriteLine(json
                        ? JsonSerializer.Serialize(new { ok = true, id = entry.Id, pid = entry.Pid, forward = entry.Forward, summary = spec.Summary })
                        : $"tunnel {entry.Id} up: {spec.Summary} (pid {entry.Pid})");
                    return 0;
                }
                catch (TunnelException ex)
                {
                    Console.Error.WriteLine("tunnel failed: " + ex.Message);
                    if (json) Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                    return 1;
                }
            }

            if (args.Contains("--tunnel-list", StringComparer.OrdinalIgnoreCase))
            {
                var tunnels = TunnelSupervisor.List();
                if (json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(tunnels.Select(entry => new
                    {
                        id = entry.Id,
                        pid = entry.Pid,
                        session = entry.SessionName,
                        spec = entry.Spec,
                        forward = entry.Forward,
                        created = entry.Created
                    })));
                }
                else if (tunnels.Count == 0)
                {
                    Console.Out.WriteLine("no tunnels");
                }
                else
                {
                    foreach (var entry in tunnels)
                        Console.Out.WriteLine($"{entry.Id}  pid {entry.Pid}  {entry.Spec}  ({entry.SessionName})");
                }
                return 0;
            }

            var tunnelDown = ValueAfter(args, "--tunnel-down");
            if (tunnelDown is not null)
            {
                var id = tunnelDown.Value.Body.Length > 0 ? tunnelDown.Value.Body : tunnelDown.Value.Key;
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.Error.WriteLine("--tunnel-down needs a tunnel id or spec — run --tunnel-list");
                    return 2;
                }
                var stopped = TunnelSupervisor.Stop(id);
                Console.Out.WriteLine(json
                    ? JsonSerializer.Serialize(new { ok = stopped, id })
                    : (stopped ? $"tunnel {id} stopped" : $"no tunnel matches '{id}'"));
                return stopped ? 0 : 1;
            }

            Console.Error.WriteLine("usage: --exec <session> <command> [--json] [--timeout n]");
            Console.Error.WriteLine("       --sftp-list <session> <path> | --sftp-get <session> <remote> <local>");
            Console.Error.WriteLine("       --sftp-put <session> <local> <remote> | --sessions | --mcp");
            return 2;
        }
        catch (AgentAccessDeniedException ex)
        {
            if (jsonWanted)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, blocked = true, error = ex.Message }));
            else
                Console.Error.WriteLine("blocked: " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "agent cli");
            if (jsonWanted)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    // ---------------------------------------------------------------- exec

    public sealed class ExecResult
    {
        public bool Ok { get; init; }
        public int Exit { get; init; }
        public string Session { get; init; } = "";
        public string Target { get; init; } = "";
        public string Command { get; init; } = "";
        public string Stdout { get; init; } = "";
        public string Stderr { get; init; } = "";
        public int DurationMs { get; init; }
        public bool TimedOut { get; init; }
        public string? Error { get; init; }

        public string ToJson() => JsonSerializer.Serialize(new
        {
            ok = Ok, exit = Exit, session = Session, target = Target, command = Command,
            stdout = Stdout, stderr = Stderr, duration_ms = DurationMs, timed_out = TimedOut, error = Error
        }, JsonOpts);

        public string ToText()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Stdout)) sb.Append(Stdout);
            if (!string.IsNullOrEmpty(Stderr))
            {
                if (sb.Length > 0 && !sb.ToString().EndsWith('\n')) sb.AppendLine();
                sb.Append(Stderr);
            }
            if (Error is not null) sb.AppendLine("error: " + Error);
            return sb.ToString();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static ExecResult RunCommand(Session session, string command, int timeoutSeconds)
    {
        var started = DateTime.UtcNow;
        var psi = session.IsLocal
            ? LocalCommand(session, command)
            : RemoteCommand(session, command, timeoutSeconds);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ExecResult
            {
                Ok = false, Exit = -1, Session = session.Id, Target = session.DisplayTarget,
                Command = command, Error = ex.Message,
                DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds
            };
        }

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        var timedOut = !process.WaitForExit(timeoutSeconds * 1000);
        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(3000); } catch { }
        }

        var stdout = SafeRead(outTask);
        var stderr = SafeRead(errTask);
        var exit = -1;
        try { exit = process.HasExited ? process.ExitCode : -1; } catch { }

        return new ExecResult
        {
            Ok = !timedOut && exit == 0,
            Exit = exit,
            Session = session.Id,
            Target = session.DisplayTarget,
            Command = command,
            Stdout = stdout,
            Stderr = stderr,
            // 124/137 is the remote watchdog saying it ran out of time.
            TimedOut = timedOut || (!session.IsLocal && (exit == 124 || exit == 137)),
            DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            Error = (timedOut || (!session.IsLocal && (exit == 124 || exit == 137)))
                ? $"timed out after {timeoutSeconds}s"
                : null
        };
    }

    private static string SafeRead(Task<string> task)
    {
        try { return task.GetAwaiter().GetResult(); }
        catch { return ""; }
    }

    private static ProcessStartInfo LocalCommand(Session session, string command)
    {
        var shell = session.LocalShell;
        if (string.IsNullOrWhiteSpace(shell))
        {
            var pwsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            shell = File.Exists(pwsh)
                ? pwsh
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                               "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        var isCmd = shell.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isCmd)
        {
            psi.Arguments = "/d /c " + command;
        }
        else
        {
            psi.Arguments = "-NoLogo -NoProfile -NonInteractive -Command " + Quote(command);
        }
        return psi;
    }

    /// <summary>
    /// The command as the remote shell should run it: under `timeout` when the host has it, so a
    /// deadline ends the process on the host instead of only closing our end of the connection.
    /// </summary>
    internal static string RemoteWatchdog(string command, int timeoutSeconds)
    {
        // Always run the command through `sh`, never through the login shell: a fish (or csh)
        // login shell cannot parse this POSIX wrapper and `--exec` dies before the command starts.
        var quoted = PosixQuote(command);
        if (timeoutSeconds <= 0) return "sh -c " + quoted;

        var seconds = Math.Max(1, timeoutSeconds - 1);   // fire just before our own kill
        var script = "if command -v timeout >/dev/null 2>&1; then "
                     + $"timeout -k 2 {seconds} sh -c {quoted}; "
                     + "else "
                     + $"sh -c {quoted}; fi";
        return "sh -c " + PosixQuote(script);
    }

    /// <summary>Single-quote for a POSIX shell: embedded quotes become '\''.</summary>
    internal static string PosixQuote(string text) =>
        "'" + text.Replace("'", "'\\''") + "'";

    private static ProcessStartInfo RemoteCommand(Session session, string command, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SshLauncher.SshExe,
            // -n: never duplicate the caller's stdin. A one-shot exec has no stdin to offer, and
            // without it Windows ssh fails at session setup with "dup() in/out/err failed" when
            // the app was started without a console (a pipe from a shell or an agent).
            // A local kill drops the connection but leaves the remote command running (verified on the
            // a real host: `sleep 20; touch marker` still created the marker after the client timed out).
            // Hand the deadline to the host as well, and keep the plain command where `timeout` is
            // missing so a minimal system behaves exactly as before.
            Arguments = "-n -T " + SshLauncher.BuildBatchArguments(session) + " "
                        + Quote(RemoteWatchdog(command, timeoutSeconds)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var askPass = CredentialVault.GetSessionSecret(session.Id) is { Length: > 0 } secret
            ? CredentialVault.StoreAskPass(session.Id, secret)
            : null;
        foreach (var pair in SshLauncher.BuildEnvironment(session, askPass))
            psi.Environment[pair.Key] = pair.Value;

        return psi;
    }

    private static string Quote(string value) => SshLauncher.QuoteArgument(value);

    // ---------------------------------------------------------------- sftp

    /// <summary>L1: upload beside the target, validate it, then replace with the old file kept.</summary>
    private static DeployResult RunDeploy(Session session, string localPath, string remotePath,
                                          string? validator, bool force, int timeoutSeconds, bool dryRun)
    {
        var staged = SafeDeploy.StagedPath(remotePath);
        var template = string.IsNullOrWhiteSpace(validator) ? SafeDeploy.ValidatorFor(remotePath) : validator;

        DeployResult Refuse(string error, string? output = null, string? command = null) => new()
        {
            Ok = false, Session = session.Id, Target = session.DisplayTarget,
            Local = localPath, Remote = remotePath, Staged = staged,
            Validated = command is not null, Validator = command, ValidatorOutput = output, Error = error
        };

        if (string.IsNullOrWhiteSpace(template) && !force)
            return Refuse(SafeDeploy.NoValidatorReason(remotePath));

        // A directory is refused before a byte is uploaded: the swap renames the target to a backup
        // name, which would move the directory itself and leave the deploy nowhere to land.
        if (!RunSftpStatChecked(session, remotePath, out var targetExists, out var targetIsDirectory, out var preflightError))
            return Refuse("could not check " + remotePath + ": " + preflightError);
        if (targetIsDirectory) return Refuse(SafeDeploy.DirectoryTargetReason(remotePath));

        if (dryRun)
        {
            var command0 = string.IsNullOrWhiteSpace(template) ? "" : SafeDeploy.ValidatorCommand(template!, staged);
            return new DeployResult
            {
                Ok = true, DryRun = true, Session = session.Id, Target = session.DisplayTarget,
                Local = localPath, Remote = remotePath, Staged = staged,
                Validator = command0.Length > 0 ? command0 : null,
                Backup = targetExists ? SafeDeploy.BackupPath(remotePath, DateTime.Now) : null
            };
        }

        long bytes;
        try
        {
            bytes = RunSftpPut(session, localPath, staged, resume: false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Refuse("upload to " + staged + " failed: " + ex.Message);
        }

        var command = string.IsNullOrWhiteSpace(template) ? "" : SafeDeploy.ValidatorCommand(template!, staged);
        string? output = null;
        if (command.Length > 0)
        {
            var check = RunCommand(session, command, timeoutSeconds);
            output = (check.Stdout + check.Stderr).Trim();
            if (!check.Ok)
            {
                try { RunSftpRemove(session, staged); } catch { }
                return Refuse("validator exited " + check.Exit, output, command);
            }
        }

        var backupPath = (string?)null;
        try
        {
            if (!RunSftpStatChecked(session, remotePath, out var exists, out var isDirectory, out var statError))
            {
                // Every other refusal removes the staged file; this one used to leave ...sshit-new behind.
                try { RunSftpRemove(session, staged); } catch { }
                return Refuse("could not check " + remotePath + ": " + statError, output, command);
            }
            if (isDirectory)
            {
                try { RunSftpRemove(session, staged); } catch { }
                return Refuse(SafeDeploy.DirectoryTargetReason(remotePath), output, command);
            }
            if (exists)
            {
                // Commit the name only after the rename: a failed first rename must not let the catch
                // restore an older backup over the live file.
                var planned = SafeDeploy.BackupPath(remotePath, DateTime.Now);
                RunSftpRename(session, remotePath, planned);
                backupPath = planned;
            }
            RunSftpRename(session, staged, remotePath);
        }
        catch (Exception ex)
        {
            // Put the original back before reporting: a half-swapped config is worse than a failed deploy.
            if (backupPath is not null)
            {
                try { RunSftpRename(session, backupPath, remotePath); } catch { }
            }
            try { RunSftpRemove(session, staged); } catch { }
            return Refuse("swap failed: " + ex.Message, output, command);
        }

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
            EvidenceChain.Append("deploy", session, new JsonObject
            {
                ["remote"] = remotePath,
                ["local"] = localPath,
                ["bytes"] = bytes,
                ["backup"] = backupPath,
                ["validator"] = command,
                ["replaced"] = backupPath is not null
            });
        }
        catch (Exception ex) { result.EvidenceNote = ex.Message; }

        return result;
    }

    /// <summary>
    /// False means "could not tell", which is not the same as "there is no file": treating a failed stat as
    /// missing skips the backup and renames straight over the original.
    /// </summary>
    private static bool RunSftpStatChecked(Session session, string path, out bool exists, out bool isDirectory,
                                            out string? error)
    {
        exists = false;
        isDirectory = false;
        error = null;
        try
        {
            using var client = new SftpClient();
            client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
            var entry = client.StatAsync(path).GetAwaiter().GetResult();
            exists = entry is not null;
            isDirectory = entry?.IsDirectory ?? false;
            // A server that omits ATTR_PERMISSIONS makes IsDirectory false for everything. Opening the
            // path as a directory is the only honest second opinion, and it is what stops the swap
            // from renaming a directory away on such a server.
            if (entry is not null && entry.Permissions is null)
            {
                try { client.ListDirectoryAsync(path).GetAwaiter().GetResult(); isDirectory = true; }
                catch { /* a file cannot be opened as a directory */ }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RunSftpRename(Session session, string from, string to)
    {
        using var client = new SftpClient();
        client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
        client.RenameAsync(from, to).GetAwaiter().GetResult();
    }

    private static void RunSftpRemove(Session session, string path)
    {
        using var client = new SftpClient();
        client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
        client.RemoveAsync(path).GetAwaiter().GetResult();
    }

    private static async Task<List<SftpEntry>> RunSftpList(Session session, string path)
    {
        using var client = new SftpClient();
        await client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id));
        var target = string.IsNullOrWhiteSpace(path) ? client.HomePath : path;
        return await client.ListDirectoryAsync(target);
    }

    private static async Task<long> RunSftpGet(Session session, string remote, string local, bool resume)
    {
        using var client = new SftpClient();
        await client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id));
        await client.DownloadAsync(remote, local, null, CancellationToken.None, resume);
        var bytes = new FileInfo(local).Exists ? new FileInfo(local).Length : 0;
        EvidenceChain.Append("transfer", session, new JsonObject
        {
            ["direction"] = "download",
            ["remote"] = remote,
            ["local"] = local,
            ["bytes"] = bytes,
            ["resumed"] = resume
        });
        return bytes;
    }

    private static async Task<List<SftpClient.MirrorItem>> RunSftpMirror(Session session, string remote, string local, bool dryRun)
    {
        using var client = new SftpClient();
        await client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id));
        return await client.MirrorAsync(remote, local, dryRun, null, CancellationToken.None);
    }

    private static async Task<long> RunSftpPut(Session session, string local, string remote, bool resume)
    {
        if (!File.Exists(local)) throw new FileNotFoundException("local file not found: " + local, local);
        using var client = new SftpClient();
        await client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id));
        await client.UploadAsync(local, remote, null, CancellationToken.None, resume);
        var bytes = new FileInfo(local).Length;
        EvidenceChain.Append("transfer", session, new JsonObject
        {
            ["direction"] = "upload",
            ["remote"] = remote,
            ["local"] = local,
            ["bytes"] = bytes,
            ["resumed"] = resume
        });
        return bytes;
    }

    private static string SftpJson(List<SftpEntry> entries) => JsonSerializer.Serialize(
        entries.Select(e => new
        {
            name = e.Name, path = e.FullPath, directory = e.IsDirectory, link = e.IsLink,
            size = e.Size, mode = e.PermissionsText,
            modified = e.Modified?.ToString("yyyy-MM-ddTHH:mm:ss")
        }));

    private static string SftpText(List<SftpEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e.IsDirectory ? "d " : (e.IsLink ? "l " : "- "));
            sb.Append(e.SizeText.PadLeft(9)).Append("  ");
            sb.Append(e.ModifiedText.PadRight(17)).Append(' ');
            sb.Append(e.FullPath).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- sessions

    internal static string BlastJson(BlastReport report) => JsonSerializer.Serialize(new
    {
        command = report.Command,
        target = report.Target,
        verdict = report.Verdict,
        items = report.TotalItems,
        directories = report.TotalDirectories,
        filesystem = report.FileSystem,
        undo = report.Undo,
        error = report.Error,
        findings = report.Findings.Select(f => new
        {
            severity = f.Severity, kind = f.Kind, detail = f.Detail, operands = f.Operands
        }),
        matches = report.Matches.Select(m => new
        {
            pattern = m.Pattern, count = m.Count, files = m.Files, directories = m.Directories
        })
    });

    internal static string BlastText(BlastReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"{report.Verdict.ToUpperInvariant()} - {report.Command}");
        foreach (var finding in report.Findings)
            text.AppendLine($"  {finding.Severity,-6} {finding.Kind,-10} {finding.Detail}");
        foreach (var match in report.Matches)
            text.AppendLine($"  {match.Pattern,-30} {match.Count} match(es)"
                            + (match.Directories.Count > 0 ? $" ({match.Directories.Count} dir)" : ""));
        if (report.Undo is not null) text.AppendLine("  undo: " + report.Undo);
        if (report.Error is not null) text.AppendLine("  ssh: " + report.Error);
        return text.ToString().TrimEnd();
    }

    internal static string DiffJson(Session leftSession, Session rightSession, CommandRun left, CommandRun right, List<DiffRow> rows) =>
        JsonSerializer.Serialize(new
        {
            command = "",
            left = new { target = left.Target, exit = left.Exit, ms = left.Milliseconds },
            right = new { target = right.Target, exit = right.Exit, ms = right.Milliseconds },
            ok = left.Exit == 0 && right.Exit == 0,
            summary = BlockDiff.Verdict(left, right, rows),
            changed = BlockDiff.Changed(rows),
            rows = rows.Select(r => new { mark = r.Mark, left = r.Left, right = r.Right })
        });

    internal static string WatchJson(CommandWatch watch) => JsonSerializer.Serialize(new
    {
        command = watch.Command,
        interval = watch.IntervalSeconds,
        changed = watch.Runs.Count(r => r.Changed),
        runs = watch.Runs.Select(r => new
        {
            index = r.Index,
            at = r.At.ToString("O"),
            exit = r.Exit,
            added = r.Added,
            removed = r.Removed,
            output = r.Output
        }),
        errors = watch.Errors
    });

    internal static string EvidenceJson(EvidenceChain.Verification verdict) => JsonSerializer.Serialize(new
    {
        ok = verdict.Ok,
        rows = verdict.Count,
        firstBreak = verdict.FirstBreak,
        breakLine = verdict.BreakLine,
        breakSeq = verdict.BreakSeq,
        key = verdict.KeyFingerprint,
        chain = verdict.Chain,
        limit = verdict.Limit
    });

    private static string EvidenceText(EvidenceChain.Verification verdict) => verdict.Ok
        ? $"evidence chain ok — {verdict.Count} row(s), key {verdict.KeyFingerprint}\n{verdict.Limit}"
        : $"evidence chain BROKEN — {verdict.FirstBreak}\n{verdict.Limit}";

    private static void EmitSessions(DataStore store, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(store.Sessions.Select(s => new
            {
                id = s.Id, name = s.Name, target = s.DisplayTarget, host = s.Host, port = s.Port,
                user = s.User, local = s.IsLocal, alias = s.ConfigAlias, tags = s.Tags
            })));
            return;
        }

        foreach (var s in store.Sessions)
            Console.Out.WriteLine($"{s.Id}  {s.Name,-24} {s.DisplayTarget}{(s.IsLocal ? "  [local]" : "")}");
    }

    // ---------------------------------------------------------------- helpers

    private static Session Resolve(DataStore store, string key, string? verb, string? command = null)
    {
        var match = store.Sessions.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
                    ?? store.Sessions.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                    ?? store.Sessions.FirstOrDefault(s => string.Equals(s.DisplayTarget, key, StringComparison.OrdinalIgnoreCase))
                    ?? store.Sessions.FirstOrDefault(s => string.Equals(s.Host, key, StringComparison.OrdinalIgnoreCase));

        var session = match ?? throw new InvalidOperationException($"no session matches '{key}' (run --sessions to list them)");
        AgentPolicy.Require(session, store.Settings);
        AgentPolicyFile.Require(session, verb, command);
        AgentLease.Require(session, verb, command);
        return session;
    }

    /// <summary>
    /// The saved sessions a fleet-wide read may touch. A leased session that does not name `posture`,
    /// or a signed policy that does not allow it, is skipped rather than probed — and the caller says
    /// so, because a silently shorter fleet reads as a smaller fleet.
    /// </summary>
    internal static List<Session> ScanTargets(DataStore store, out List<string> skipped)
    {
        var allowed = new List<Session>();
        skipped = new List<string>();
        foreach (var session in store.Sessions)
        {
            if (AgentLease.Allows(session, "posture", null, out _)
                && AgentPolicyFile.Allows(session, "posture", null, out _)) allowed.Add(session);
            else skipped.Add(session.Id);
        }
        return allowed;
    }

    /// <summary>Reads a bounded minute count; a lease that never expires is not a lease.</summary>
    private static int MinutesAfter(string[] args, string flag, int fallback)
    {
        var raw = OptionalValueAfter(args, flag);
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < 1 || value > AgentLease.MaxMinutes)
            throw new InvalidOperationException($"{flag} must be 1..{AgentLease.MaxMinutes} minutes");
        return value;
    }

    /// <summary>Like ValueAfter, but returns null when the verb stands alone (the next token is another flag).</summary>
    private static string? OptionalValueAfter(string[] args, string verb)
    {
        var index = Array.FindIndex(args, a => a.Equals(verb, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;
        var next = args[index + 1];
        return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
    }

    /// <summary>Returns the value after a verb plus everything that follows it as one string.</summary>
    private static (string Key, string Body)? ValueAfter(string[] args, string verb)
    {
        var index = Array.FindIndex(args, a => a.Equals(verb, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;

        var key = args[index + 1];
        var rest = new List<string>();
        for (var i = index + 2; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal)) break;
            rest.Add(a);
        }
        return (key, string.Join(' ', rest));
    }

    private static string[] SplitRest(string rest, int count)
    {
        // Honour double quotes so a path WITH SPACES can be the first operand (a path under Program
        // Files, for example). The last operand always takes the remainder, so an unquoted remote
        // path keeps working exactly as before.
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in rest)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c) && parts.Count < count - 1)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString().Trim());

        if (parts.Count < count)
            throw new InvalidOperationException($"expected {count} arguments, got {parts.Count}");
        return parts.ToArray();
    }
}

/// <summary>
/// Stdio MCP server (JSON-RPC 2.0, one object per line). It exposes the SSH-it agent surface to an
/// MCP client without a shell, behind the same gate, signed policy and lease as the CLI verbs.
///
/// Protocol: 2025-06-18, with 2025-03-26 and 2024-11-05 still accepted so an older client keeps the
/// revision it asked for. That revision is what lets a client tell a read-only tool from a destructive
/// one (annotations), read a result as data instead of a JSON string (structuredContent), and see a
/// failed call as a tool result rather than a transport error (isError).
///
/// Deliberately absent: server-initiated messages, resources, prompts, progress, cancellation and
/// listChanged. SAY SO rather than implying them — a client that waits for a progress notification
/// will wait forever.
/// </summary>
public static class McpServer
{
    private const string LatestProtocol = "2025-06-18";

    private static readonly string[] SupportedProtocols = { "2025-06-18", "2025-03-26", "2024-11-05" };

    /// <summary>What each tool is, and whether a client may run it without asking. One table so a new
    /// tool cannot ship unlabelled — the test suite checks every entry has annotations.</summary>
    private static readonly Dictionary<string, (string Title, bool ReadOnly, bool Destructive, bool Idempotent)> Hints = new()
    {
        ["ssh_exec"] = ("Run a command", false, true, false),
        ["sftp_list"] = ("List a remote directory", true, false, true),
        ["sftp_get"] = ("Download a file", true, false, true),
        ["sftp_put"] = ("Upload a file", false, true, true),
        ["sessions_list"] = ("List sessions", true, false, true),
        ["tunnel_up"] = ("Open a tunnel", false, false, false),
        ["tunnel_list"] = ("List tunnels", true, false, true),
        ["tunnel_down"] = ("Close a tunnel", false, false, true),
        ["posture_scan"] = ("Scan host posture", true, false, true),
        ["evidence_verify"] = ("Verify the evidence chain", true, false, true),
        ["lease_status"] = ("Show leases", true, false, true),
        ["queue_list"] = ("List queued transfers", true, false, true),
        ["queue_add"] = ("Queue a transfer", false, false, false),
        ["service_list"] = ("List services", true, false, true),
        ["snapshot_list"] = ("List undo points", true, false, true),
        ["blast"] = ("Preview a destructive command", true, false, true),
        ["diff"] = ("Compare command output", true, false, true),
        ["snapshot_take"] = ("Take an undo snapshot", false, false, false),
        ["policy_show"] = ("Show the agent policy", true, false, true),
        ["session_observe"] = ("Observe a live pane", true, false, true),
        ["blocks_since"] = ("Read terminal blocks", true, false, true),
        ["session_wait"] = ("Wait for a pane state", true, false, true),
        ["session_type"] = ("Type into a live pane", false, true, false)
    };

    public static int Run()
    {
        var store = new DataStore();
        store.Load();

        var gate = new object();
        var workers = new List<Task>();

        while (true)
        {
            string? line;
            try { line = Console.In.ReadLine(); }
            catch { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // A batch is a JSON array. The old Parse() threw on it and took the whole server down.
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(line) is System.Text.Json.Nodes.JsonArray)
                {
                    Write(ErrorResponse(null, -32600, "batching is not supported; send one request per line"), gate);
                    continue;
                }
            }
            catch { }

            var request = Parse(line);
            if (request is null)
            {
                Write(ErrorResponse(null, -32700, "parse error: the line is not JSON"), gate);
                continue;
            }

            // tools/call runs off the read loop: one long ssh_exec must not freeze every other call.
            if (Method(request) == "tools/call")
            {
                var copy = request.DeepClone();
                workers.Add(Task.Run(() => Write(Handle(copy, store), gate)));
                workers.RemoveAll(task => task.IsCompleted);
                continue;
            }

            Write(Handle(request, store), gate);
        }

        try { Task.WaitAll(workers.ToArray(), 10000); } catch { }
        return 0;
    }

    /// <summary>One request in, one response out; null for a notification. Exposed so the self-test can
    /// drive the protocol without a console.</summary>
    public static string? HandleLine(string line, DataStore store)
    {
        var request = Parse(line);
        if (request is null) return ErrorResponse(null, -32700, "parse error: the line is not JSON").ToJsonString();
        var response = Handle(request, store);
        return response?.ToJsonString();
    }

    private static JsonObject? Handle(JsonNode request, DataStore store)
    {
        var id = request["id"];
        var method = Method(request);
        try
        {
            var result = Dispatch(store, method, request["params"] as JsonObject);
            if (result is null || id is null) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
        }
        catch (McpRequestException ex)
        {
            return id is null ? null : ErrorResponse(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "mcp " + method);
            return id is null ? null : ErrorResponse(id, -32603, ex.Message);
        }
    }

    private static JsonNode? Dispatch(DataStore store, string method, JsonObject? p)
    {
        // A notification is never answered, and an unknown $/ extension is ignored rather than failed.
        if (method.StartsWith("notifications/", StringComparison.Ordinal)
            || method.StartsWith("$/", StringComparison.Ordinal)) return null;

        switch (method)
        {
            case "initialize":
            {
                var requested = p?["protocolVersion"]?.GetValue<string>();
                var version = requested is not null && SupportedProtocols.Contains(requested)
                    ? requested
                    : LatestProtocol;

                return new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                        ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false }
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "ssh-it",
                        ["title"] = "SSH-it",
                        ["version"] = ServerVersion()
                    },
                    ["instructions"] = "SSH-it's own sessions, keys, jump hosts and stored secrets, behind the "
                                       + "agent gate, the signed policy and leases. Every session-resolving call "
                                       + "names a verb and is refused (isError) when no grant covers it. Tools run "
                                       + "one at a time per call; a long command does not block other requests."
                };
            }

            case "ping":
                return new JsonObject();

            case "tools/list":
                return new JsonObject { ["tools"] = Tools() };

            case "tools/call":
                return CallTool(store, p);

            case "resources/list":
                return ListResources();

            case "resources/templates/list":
                return ResourceTemplates();

            case "resources/read":
                return ReadResource(p);
        }

        throw new McpRequestException(-32601, "method not found: " + method);
    }

    /// <summary>One resource per open pane; the MCP process asks the GUI which panes exist.</summary>
    private static JsonNode ListResources()
    {
        var frames = AgentBridge.Call(new JsonObject { ["op"] = "list" }, 5000);
        var array = new JsonArray();
        if (frames["panes"] is JsonArray panes)
        {
            foreach (var pane in panes.OfType<JsonObject>())
            {
                var id = pane["id"]?.GetValue<string>() ?? "";
                if (id.Length == 0) continue;
                array.Add(new JsonObject
                {
                    ["uri"] = AgentBridge.BlocksUri(id),
                    ["name"] = (pane["name"]?.GetValue<string>() ?? id) + " — command blocks",
                    ["description"] = "Finished command blocks (command, exit, duration, output) for this pane.",
                    ["mimeType"] = "application/json"
                });
            }
        }
        return new JsonObject { ["resources"] = array };
    }

    private static JsonNode ResourceTemplates() => new JsonObject
    {
        ["resourceTemplates"] = new JsonArray(new JsonObject
        {
            ["uriTemplate"] = "terminal://session/{session}/blocks",
            ["name"] = "Pane command blocks",
            ["description"] = "Blocks a pane has finished, oldest first. Same data as blocks_since.",
            ["mimeType"] = "application/json"
        })
    };

    private static JsonNode ReadResource(JsonObject? p)
    {
        var uri = p?["uri"]?.GetValue<string>()
                  ?? throw new McpRequestException(-32602, "resources/read needs a uri");
        var session = AgentBridge.SessionFromUri(uri)
                      ?? throw new McpRequestException(-32002, "not an SSH-it resource: " + uri);
        var frame = AgentBridge.Call(new JsonObject
        {
            ["op"] = "blocks", ["session"] = session, ["since"] = 0, ["limit"] = 100
        });
        if (frame["ok"]?.GetValue<bool>() != true)
            throw new McpRequestException(-32002, frame["error"]?.GetValue<string>() ?? "the pane is not open");
        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["uri"] = uri,
                ["mimeType"] = "application/json",
                ["text"] = frame.ToJsonString()
            })
        };
    }

    private static string ServerVersion()
        => typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    // ---------------------------------------------------------------- tools

    private static JsonArray Tools()
    {
        var tools = CoreTools();
        foreach (var extra in ExtraTools()) tools.Add(extra?.DeepClone());
        foreach (var node in tools)
        {
            if (node is not JsonObject tool) continue;
            var name = tool["name"]?.GetValue<string>() ?? "";
            if (!Hints.TryGetValue(name, out var hint)) continue;

            tool["title"] = hint.Title;
            tool["annotations"] = new JsonObject
            {
                ["title"] = hint.Title,
                ["readOnlyHint"] = hint.ReadOnly,
                ["destructiveHint"] = hint.Destructive,
                ["idempotentHint"] = hint.Idempotent,
                ["openWorldHint"] = true
            };
        }
        return tools;
    }

    private static JsonArray CoreTools() => new(
        Tool("ssh_exec", "Run a command on a saved session and return stdout, stderr and the exit code.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host as shown in SSH-it."),
                ["command"] = Prop("string", "Command to run."),
                ["timeout_s"] = Prop("integer", "Seconds before the command is killed (default 60).")
            }, "session", "command"),
        Tool("sftp_list", "List a remote directory on a saved session.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["path"] = Prop("string", "Remote path (defaults to the login directory).")
            }, "session"),
        Tool("sftp_get", "Download a remote file from a saved session to a local path.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["remote"] = Prop("string", "Remote file path."),
                ["local"] = Prop("string", "Local destination path.")
            }, "session", "remote", "local"),
        Tool("sftp_put", "Upload a local file to a saved session.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["local"] = Prop("string", "Local source path."),
                ["remote"] = Prop("string", "Remote destination path.")
            }, "session", "local", "remote"),
        Tool("sessions_list", "List the sessions SSH-it knows about.",
            new JsonObject()),
        Tool("tunnel_up", "Start a detached ssh -N forward. It keeps running after this call returns.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["spec"] = Prop("string", "L:bind:port:host:port, R:... or D:bind:port.")
            }, "session", "spec"),
        Tool("tunnel_list", "List the tunnels still running from earlier agent calls.",
            new JsonObject()),
        Tool("tunnel_down", "Stop a tunnel by id or by its spec.",
            new JsonObject
            {
                ["id"] = Prop("string", "Tunnel id, or the spec it was started with.")
            }, "id"),
        Tool("posture_scan", "Read the algorithms and server settings a saved host negotiates, score the weak ones and return the fix for each. Read-only; omit the session to scan every saved SSH host.",
            new JsonObject
            {
                ["session"] = Prop("string", "Optional session id, name or host. Omit to scan all saved SSH hosts.")
            }),
        Tool("evidence_verify", "Verify the signed local record of what this client did and name the first broken row, if any.",
            new JsonObject()),
        Tool("lease_status", "Show the signed leases and approvals that scope this surface: verbs, exact commands, what is active, what expires when. Read-only.", new JsonObject()),
        Tool("queue_list", "List the persistent transfer queue: kind, session, paths, state and bytes. Read-only.",
            new JsonObject()),
        Tool("queue_add", "Queue a get or put to run later. The lease and the signed policy apply when the job runs, not when it is added.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["kind"] = Prop("string", "'get' or 'put' (default get)."),
                ["remote"] = Prop("string", "Remote path."),
                ["local"] = Prop("string", "Local path.")
            }, "session", "remote", "local"),
        Tool("service_list", "List the named service tunnels: name, forward spec, DSN template and health check. Read-only.",
            new JsonObject()),
        Tool("snapshot_list", "List the undo points recorded on this machine (btrfs/zfs/copy), newest first, with the restore command for each. Read-only.",
            new JsonObject
            {
                ["session"] = Prop("string", "Optional session id, name or host to filter by.")
            }));

    private static JsonArray ExtraTools() => new(
        Tool("blast", "Preview what a destructive command would touch before it runs: findings, the host-expanded operands with counts and names, and the filesystem-aware undo hint. Read-only.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["command"] = Prop("string", "The command that would run.")
            }, "session", "command"),
        Tool("diff", "Run one command twice — on the same host or on two — and return an aligned side-by-side diff of the output.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session to run on."),
                ["command"] = Prop("string", "Command to run on both sides."),
                ["against"] = Prop("string", "Optional second session; omit to compare the same host over time.")
            }, "session", "command"),
        Tool("snapshot_take", "Take an undo snapshot of a path or glob on the host (btrfs subvolume, zfs snapshot, or a reflink copy) and return the restore command. It does not restore anything.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host."),
                ["path"] = Prop("string", "File, directory or glob to snapshot.")
            }, "session", "path"),
        Tool("policy_show", "Show the signed agent policy that scopes this surface: verbs, hosts, path rules, destructive handling and approver names. Read-only.",
            new JsonObject()),
        Tool("session_observe", "Observe an OPEN pane in the running SSH-it window: state, prompt readiness, block count, last sequence number and the last few blocks. Read-only; the GUI must be running.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host whose pane is open."),
                ["ttl_seconds"] = Prop("integer", "How long this observation is meant to stay valid (default 60)."),
                ["redact"] = Prop("array", "Optional regular expressions whose matches become [redacted].")
            }, "session"),
        Tool("blocks_since", "Read the finished command blocks of an open pane after a sequence number. Start from the lastSeq session_observe returned.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host whose pane is open."),
                ["seq"] = Prop("integer", "Return blocks with a seq greater than this (0 = from the oldest kept)."),
                ["limit"] = Prop("integer", "Most blocks to return (default 20, max 200)."),
                ["observer"] = Prop("string", "The observer id from session_observe; refused once its ttl expires."),
                ["redact"] = Prop("array", "Optional regular expressions whose matches become [redacted].")
            }, "session"),
        Tool("session_wait", "Wait until an open pane reaches a prompt, finishes the next block, or prints a pattern. Polls the live pane without blocking the GUI.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host whose pane is open."),
                ["until"] = Prop("string", "prompt | exit | pattern"),
                ["pattern"] = Prop("string", "Text to look for when until=pattern."),
                ["since"] = Prop("integer", "For until=exit: the seq to wait past (default: the newest block at call time)."),
                ["timeout_ms"] = Prop("integer", "Give up after this many milliseconds (default 30000)."),
                ["observer"] = Prop("string", "The observer id from session_observe; the wait cannot outlive it.")
            }, "session"),
        Tool("session_type", "Type text into an OPEN pane, bounded by the same lease/policy gate as ssh_exec. A refused call returns blocked:true and the bytes never reach the pty.",
            new JsonObject
            {
                ["session"] = Prop("string", "Session id, name or host whose pane is open."),
                ["text"] = Prop("string", "Text to type (a command, not keystrokes)."),
                ["enter"] = Prop("boolean", "Press Enter after the text (default true).")
            }, "session", "text"));

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonNode CallTool(DataStore store, JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new McpRequestException(-32602, "tools/call needs a name");
        if (!Hints.ContainsKey(name)) throw new McpRequestException(-32602, "unknown tool: " + name);

        var args = p?["arguments"] as JsonObject ?? new JsonObject();

        string Text(string key) => args[key]?.GetValue<string>()
            ?? throw new McpRequestException(-32602, "missing argument '" + key + "'");

        var pathArg = args["path"]?.GetValue<string>();
        Session Session(string verb, string? command = null) => ResolveSession(store, Text("session"), verb, command);

        try
        {
            return name switch
            {
                "ssh_exec" => Result(AgentCli.RunCommand(
                    Session("exec", Text("command")), Text("command"), Timeout(args)).ToJson()),

                "sftp_list" => Result(RunList(Session("sftp-list", pathArg), pathArg ?? "")),
                "sftp_get" => Result(RunGet(Session("sftp-get", Text("remote")), Text("remote"), Text("local"))),
                "sftp_put" => Result(RunPut(Session("sftp-put", Text("remote")), Text("local"), Text("remote"))),
                "sessions_list" => Result(SessionsJson(store)),
                "lease_status" => Result(AgentLease.ListText(true)),
                "tunnel_up" => Result(TunnelJson(TunnelSupervisor.Start(Session("tunnel-up", Text("spec")), Text("spec")))),
                "tunnel_list" => Result(TunnelsJson()),
                "tunnel_down" => Result(TunnelDown(Text("id"))),
                "posture_scan" => Result(PostureJson(store, args["session"]?.GetValue<string>())),
                "evidence_verify" => Result(AgentCli.EvidenceJson(EvidenceChain.Verify())),
                "queue_list" => Result(TransferQueue.ListText(true)),
                "queue_add" => Result(QueueAdd(store, args)),
                "service_list" => Result(ServiceTunnels.ListText(true)),
                "snapshot_list" => Result(SnapshotJson(store, args["session"]?.GetValue<string>())),
                "blast" => Result(RunBlast(Session("blast", Text("command")), Text("command"))),
                "diff" => Result(RunDiff(store, Session("diff", Text("command")), Text("command"),
                    args["against"]?.GetValue<string>())),
                "snapshot_take" => Result(TakeSnapshot(Session("snapshot", Text("path")), Text("path"))),
                "policy_show" => Result(PolicyJson()),
                // G2: these four do not dial anything. They observe the panes that are already open
                // in the GUI process through the per-user bridge; `session_type` is gated there.
                "session_observe" => Result(AgentBridge.Call(new JsonObject
                {
                    ["op"] = "observe", ["session"] = Text("session"),
                    ["ttlSeconds"] = args["ttl_seconds"]?.GetValue<long>() ?? 60,
                    ["redact"] = args["redact"] as JsonArray ?? new JsonArray()
                }).ToJsonString()),
                "blocks_since" => Result(AgentBridge.Call(new JsonObject
                {
                    ["op"] = "blocks", ["session"] = Text("session"),
                    ["since"] = args["seq"]?.GetValue<long>() ?? 0,
                    ["limit"] = args["limit"]?.GetValue<long>() ?? 20,
                    ["observer"] = args["observer"]?.GetValue<string>() ?? "",
                    ["redact"] = args["redact"] as JsonArray ?? new JsonArray()
                }).ToJsonString()),
                "session_wait" => Result(AgentBridge.Call(new JsonObject
                {
                    ["op"] = "wait", ["session"] = Text("session"),
                    ["until"] = args["until"]?.GetValue<string>() ?? "prompt",
                    ["pattern"] = args["pattern"]?.GetValue<string>() ?? "",
                    ["timeoutMs"] = args["timeout_ms"]?.GetValue<long>() ?? 30000,
                    ["since"] = args["since"]?.GetValue<long>() ?? -1,
                    ["observer"] = args["observer"]?.GetValue<string>() ?? ""
                }, timeoutMs: (int)Math.Clamp((args["timeout_ms"]?.GetValue<long>() ?? 30000) + 5000, 5000, 310000))
                    .ToJsonString()),
                "session_type" => Result(AgentBridge.Call(new JsonObject
                {
                    ["op"] = "type", ["session"] = Text("session"), ["text"] = Text("text"),
                    ["enter"] = args["enter"]?.GetValue<bool>() ?? true
                }).ToJsonString()),
                _ => throw new McpRequestException(-32602, "unknown tool: " + name)
            };
        }
        catch (McpRequestException)
        {
            throw;                       // a bad request, not a failed tool
        }
        catch (Exception ex)
        {
            // A refusal (gate, policy, lease), a dead host or a missing file is the tool failing.
            Log.Warn("mcp tool " + name + ": " + ex.Message);
            return ErrorResult(ex.Message);
        }
    }

    private static string RunBlast(Session session, string command)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return AgentCli.BlastJson(BlastRadius.PreviewAsync(session, command, cts.Token).GetAwaiter().GetResult());
    }

    private static string RunDiff(DataStore store, Session left, string command, string? againstKey)
    {
        var right = againstKey is null ? left : ResolveSession(store, againstKey, "diff", command);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var first = BlockDiff.RunAsync(left, command, cts.Token).GetAwaiter().GetResult();
        var second = BlockDiff.RunAsync(right, command, cts.Token).GetAwaiter().GetResult();
        var rows = BlockDiff.Align(first.Output, second.Output);
        return AgentCli.DiffJson(left, right, first, second, rows);
    }

    private static string TakeSnapshot(Session session, string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (point, error) = UndoStore.SnapshotAsync(session, path, cts.Token).GetAwaiter().GetResult();
        if (point is null) throw new InvalidOperationException("no snapshot: " + error);
        return JsonSerializer.Serialize(new
        {
            ok = true, id = point.Id, path = point.Path, snapshot = point.Snapshot,
            kind = point.Kind, restore = point.Restore
        });
    }

    private static string PolicyJson()
    {
        var loaded = AgentPolicyFile.Load();
        return JsonSerializer.Serialize(new
        {
            ok = loaded.Ok,
            error = loaded.Error,
            file = AgentPolicyFile.PolicyPath,
            verbs = loaded.Rules?.Verbs,
            hosts = loaded.Rules?.Hosts,
            allowPaths = loaded.Rules?.AllowPaths,
            denyPaths = loaded.Rules?.DenyPaths,
            destructive = loaded.Rules?.Destructive,
            approvers = loaded.Rules?.Approvers.Select(a => a.Name).ToArray(),
            updated = loaded.Rules?.Updated.ToString("O")
        });
    }

    private static int Timeout(JsonObject args)
        => args["timeout_s"] is JsonNode node && int.TryParse(node.ToString(), out var seconds) ? seconds : 60;

    private static string QueueAdd(DataStore store, JsonObject args)
    {
        string Required(string key) => args[key]?.GetValue<string>()
            ?? throw new McpRequestException(-32602, "missing argument '" + key + "'");

        var kind = (args["kind"]?.GetValue<string>() ?? "get").ToLowerInvariant();
        if (kind is not ("get" or "put"))
            throw new McpRequestException(-32602, "kind must be 'get' or 'put'");

        var remote = Required("remote");
        var local = Required("local");
        var session = ResolveSession(store, Required("session"), kind == "get" ? "sftp-get" : "sftp-put", remote);
        var job = TransferQueue.Add(session, kind, remote, local);
        return JsonSerializer.Serialize(job);
    }

    private static string SnapshotJson(DataStore store, string? key)
    {
        var sessionId = key is null ? null : ResolveSession(store, key, "snapshot").Id;
        return JsonSerializer.Serialize(UndoStore.List(sessionId));
    }

    /// <summary>A tool result: the text block every client understands, plus the same payload as data.</summary>
    private static JsonObject Result(string payload)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload }),
            ["isError"] = false
        };

        var structured = AsObject(payload);
        if (structured is not null) result["structuredContent"] = structured;
        return result;
    }

    private static JsonObject ErrorResult(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true
    };

    /// <summary>structuredContent must be an object, so an array payload is wrapped as {"items": [...]}.</summary>
    private static JsonObject? AsObject(string payload)
    {
        try
        {
            var node = JsonNode.Parse(payload);
            return node switch
            {
                JsonObject obj => (JsonObject)obj.DeepClone(),
                JsonArray array => new JsonObject { ["items"] = array.DeepClone() },
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static JsonNode? Parse(string line)
    {
        try { return JsonNode.Parse(line); }
        catch (Exception ex)
        {
            Log.Warn("mcp: bad json: " + ex.Message);
            return null;
        }
    }

    private static string Method(JsonNode request)
    {
        try { return request["method"]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static void Write(JsonObject? payload, object gate)
    {
        if (payload is null) return;
        lock (gate)
        {
            Console.Out.WriteLine(payload.ToJsonString());
            Console.Out.Flush();
        }
    }

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        if (id is not null) response["id"] = id.DeepClone();
        return response;
    }

    /// <summary>A request-level problem (bad method, unknown tool, missing argument) — a JSON-RPC error,
    /// never a tool result.</summary>
    private sealed class McpRequestException : Exception
    {
        public int Code { get; }

        public McpRequestException(int code, string message) : base(message) => Code = code;
    }

    /// <summary>Fleet posture for one saved host, or for all of them when no key is given.</summary>
    private static string PostureJson(DataStore store, string? key)
    {
        var skipped = new List<string>();
        var sessions = key is null
            ? AgentCli.ScanTargets(store, out skipped)
            : new List<Session> { ResolveSession(store, key, "posture") };
        if (skipped.Count > 0)
            Console.Error.WriteLine("skipped " + skipped.Count + " session(s) a lease or the policy does not allow posture on: "
                                    + string.Join(", ", skipped));
        var hosts = FleetPosture.ScanAsync(sessions, null, null, CancellationToken.None).GetAwaiter().GetResult();
        return FleetPosture.ToJson(hosts);
    }

    private static Session ResolveSession(DataStore store, string key, string? verb, string? command = null)
    {
        var match = store.Sessions.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? store.Sessions.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? store.Sessions.FirstOrDefault(s => string.Equals(s.Host, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"no session matches '{key}'");

        AgentPolicy.Require(match, store.Settings);
        AgentPolicyFile.Require(match, verb, command);
        AgentLease.Require(match, verb, command);
        return match;
    }

    private static string SessionsJson(DataStore store) => JsonSerializer.Serialize(store.Sessions.Select(s => new
    {
        id = s.Id, name = s.Name, target = s.DisplayTarget, local = s.IsLocal
    }));

    private static string TunnelJson(TunnelSupervisor.Entry entry) => JsonSerializer.Serialize(new
    {
        ok = true,
        id = entry.Id,
        pid = entry.Pid,
        session = entry.SessionName,
        spec = entry.Spec,
        forward = entry.Forward
    });

    private static string TunnelsJson() => JsonSerializer.Serialize(TunnelSupervisor.List().Select(entry => new
    {
        id = entry.Id,
        pid = entry.Pid,
        session = entry.SessionName,
        spec = entry.Spec,
        forward = entry.Forward,
        created = entry.Created
    }));

    private static string TunnelDown(string idOrSpec) => JsonSerializer.Serialize(new
    {
        ok = TunnelSupervisor.Stop(idOrSpec),
        id = idOrSpec
    });

    private static string RunList(Session session, string path)
    {
        using var client = new SftpClient();
        client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
        var target = string.IsNullOrWhiteSpace(path) ? client.HomePath : path;
        var entries = client.ListDirectoryAsync(target).GetAwaiter().GetResult();
        return JsonSerializer.Serialize(entries.Select(e => new
        {
            name = e.Name, path = e.FullPath, directory = e.IsDirectory, size = e.Size
        }));
    }

    private static string RunGet(Session session, string remote, string local)
    {
        using var client = new SftpClient();
        client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
        client.DownloadAsync(remote, local, null, CancellationToken.None).GetAwaiter().GetResult();
        return JsonSerializer.Serialize(new { ok = true, remote, local, bytes = new FileInfo(local).Length });
    }

    private static string RunPut(Session session, string local, string remote)
    {
        if (!File.Exists(local)) throw new FileNotFoundException("local file not found: " + local, local);
        using var client = new SftpClient();
        client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
        client.UploadAsync(local, remote, null, CancellationToken.None).GetAwaiter().GetResult();
        return JsonSerializer.Serialize(new { ok = true, local, remote, bytes = new FileInfo(local).Length });
    }
}
