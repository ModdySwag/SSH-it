using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One queued transfer: what to move, where, and how far it got.</summary>
public sealed class TransferJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Kind { get; set; } = "get";          // get | put
    public string SessionId { get; set; } = "";
    public string SessionName { get; set; } = "";
    public string Remote { get; set; } = "";
    public string Local { get; set; } = "";
    public long Bytes { get; set; } = -1;
    public string State { get; set; } = "queued";       // queued | running | done | failed | cancelled
    public string Error { get; set; } = "";
    public string Added { get; set; } = "";
    public string Started { get; set; } = "";
    public string Finished { get; set; } = "";

    public string Describe()
        => Id + "  " + Kind + "  " + SessionName + "  "
           + (Kind == "get" ? Remote + " → " + Local : Local + " → " + Remote)
           + "  " + State + (Bytes >= 0 ? "  " + Bytes + " bytes" : "")
           + (Error.Length > 0 ? "  — " + Error : "");
}

/// <summary>
/// R3-22: a persistent transfer queue. Jobs are added, stored, and run one at a time in order, so a
/// script can enqueue work and a later command can drain it — and one failed transfer does not take
/// the rest of the queue down with it.
///
/// Honest limits: serial by design (one connection per job, no parallelism), get/put only (a mirror
/// is still its own verb), no resume switch per job yet (the SFTP resume setting applies), and no
/// live progress inside a running job — the queue reports bytes once the job finishes.
/// </summary>
public static class TransferQueue
{
    public static string StorePath => Path.Combine(DataStore.RootPath, "queue.json");

    public static List<TransferJob> Load(string? path = null)
    {
        path ??= StorePath;
        var jobs = new List<TransferJob>();
        if (!File.Exists(path)) return jobs;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document) return jobs;
            if (document["jobs"] is not JsonArray rows) return jobs;
            foreach (var row in rows.OfType<JsonObject>())
                jobs.Add(new TransferJob
                {
                    Id = row["id"]?.GetValue<string>() ?? "",
                    Kind = row["kind"]?.GetValue<string>() ?? "get",
                    SessionId = row["session"]?.GetValue<string>() ?? "",
                    SessionName = row["name"]?.GetValue<string>() ?? "",
                    Remote = row["remote"]?.GetValue<string>() ?? "",
                    Local = row["local"]?.GetValue<string>() ?? "",
                    Bytes = row["bytes"]?.GetValue<long>() ?? -1,
                    State = row["state"]?.GetValue<string>() ?? "queued",
                    Error = row["error"]?.GetValue<string>() ?? "",
                    Added = row["added"]?.GetValue<string>() ?? "",
                    Started = row["started"]?.GetValue<string>() ?? "",
                    Finished = row["finished"]?.GetValue<string>() ?? ""
                });
        }
        catch (Exception ex)
        {
            Log.Warn("queue: " + ex.Message);
        }
        return jobs;
    }

    public static void Save(List<TransferJob> jobs, string? path = null)
    {
        path ??= StorePath;
        var rows = new JsonArray();
        foreach (var job in jobs)
            rows.Add(new JsonObject
            {
                ["id"] = job.Id,
                ["kind"] = job.Kind,
                ["session"] = job.SessionId,
                ["name"] = job.SessionName,
                ["remote"] = job.Remote,
                ["local"] = job.Local,
                ["bytes"] = job.Bytes,
                ["state"] = job.State,
                ["error"] = job.Error,
                ["added"] = job.Added,
                ["started"] = job.Started,
                ["finished"] = job.Finished
            });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary,
            new JsonObject { ["version"] = 1, ["jobs"] = rows }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Adds a job. A put checks that its local file exists now, not an hour later at run time.</summary>
    public static TransferJob Add(Session session, string kind, string remote, string local, string? path = null)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind is not ("get" or "put"))
            throw new InvalidOperationException("queue kinds are get or put");
        remote = remote.Trim();
        local = local.Trim();
        if (remote.Length == 0 || local.Length == 0)
            throw new InvalidOperationException("a queued transfer needs both a remote and a local path");
        if (kind == "put" && !File.Exists(local))
            throw new FileNotFoundException("local file not found: " + local, local);

        var jobs = Load(path);
        var job = new TransferJob
        {
            Kind = kind,
            SessionId = session.Id,
            SessionName = session.DisplayTarget,
            Remote = remote,
            Local = local,
            Added = DateTimeOffset.UtcNow.ToString("O")
        };
        jobs.Add(job);
        Save(jobs, path);
        return job;
    }

    public static int Clear(string? path = null)
    {
        var jobs = Load(path);
        var finished = jobs.RemoveAll(j => j.State is "done" or "failed" or "cancelled");
        if (finished > 0) Save(jobs, path);
        return finished;
    }

    /// <summary>
    /// Runs every queued job in order. One job's failure is recorded and the next still runs; the
    /// return value is the number that failed.
    /// </summary>
    public static int Run(Func<TransferJob, Session> resolve, List<TransferJob> jobs, string? path = null,
        Action<TransferJob>? progress = null, bool resume = false, Action<TransferJob>? perform = null)
    {
        var failed = 0;
        foreach (var job in jobs.Where(j => j.State == "queued").ToList())
        {
            Session session;
            try { session = resolve(job); }
            catch (AgentAccessDeniedException ex)
            {
                // A refusal is a result: record it against the job and keep the queue moving.
                job.State = "failed";
                job.Error = "blocked: " + ex.Message;
                job.Finished = DateTimeOffset.UtcNow.ToString("O");
                Save(jobs, path);
                failed++;
                progress?.Invoke(job);
                continue;
            }

            job.State = "running";
            job.Started = DateTimeOffset.UtcNow.ToString("O");
            Save(jobs, path);
            progress?.Invoke(job);

            try
            {
                if (perform is not null)
                {
                    perform(job);                       // the selftest's stand-in for the wire
                }
                else
                {
                    using var client = new SftpClient();
                    client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id)).GetAwaiter().GetResult();
                    if (job.Kind == "get")
                        client.DownloadAsync(job.Remote, job.Local, null, CancellationToken.None).GetAwaiter().GetResult();
                    else
                        client.UploadAsync(job.Local, job.Remote, null, CancellationToken.None).GetAwaiter().GetResult();
                }

                job.Bytes = File.Exists(job.Local) ? new FileInfo(job.Local).Length : job.Bytes;
                job.State = "done";
                job.Error = "";
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.Error = ex.Message.Length > 200 ? ex.Message.Substring(0, 200) : ex.Message;
                failed++;
            }
            finally
            {
                job.Finished = DateTimeOffset.UtcNow.ToString("O");
                Save(jobs, path);
                progress?.Invoke(job);
            }
        }

        return failed;
    }

    public static string ListText(bool json, string? path = null)
    {
        var jobs = Load(path);
        if (json)
            return JsonSerializer.Serialize(new
            {
                jobs = jobs.Select(j => new
                {
                    id = j.Id, kind = j.Kind, session = j.SessionId, name = j.SessionName,
                    remote = j.Remote, local = j.Local, bytes = j.Bytes, state = j.State,
                    error = j.Error, added = j.Added, started = j.Started, finished = j.Finished
                })
            });

        if (jobs.Count == 0) return "the queue is empty (--queue-add <session> get|put <remote> <local>)";
        var counts = jobs.GroupBy(j => j.State).OrderBy(g => g.Key)
            .Select(g => g.Count() + " " + g.Key);
        return jobs.Count + " job(s): " + string.Join(", ", counts) + "\n"
               + string.Join("\n", jobs.Select(j => j.Describe()));
    }
}
