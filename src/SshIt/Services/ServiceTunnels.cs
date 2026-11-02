using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>A named forward: what it is for, how to reach it, and how to tell whether it is alive.</summary>
public sealed class ServiceDefinition
{
    public string Name { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Spec { get; set; } = "";
    public string Dsn { get; set; } = "";           // template; empty = tcp://{host}:{port}
    public string CheckCommand { get; set; } = "";  // run on the host after the forward is up
    public string Note { get; set; } = "";
    public string Created { get; set; } = "";
}

/// <summary>What came back from starting or checking a named service.</summary>
public sealed class ServiceReport
{
    public string Name { get; set; } = "";
    public string Spec { get; set; } = "";
    public string Dsn { get; set; } = "";
    public string TunnelId { get; set; } = "";
    public int Pid { get; set; }
    public string Status { get; set; } = "unknown";   // up | down | unknown
    public string Detail { get; set; } = "";
    public int Millis { get; set; }
    public string CheckOutput { get; set; } = "";
}

/// <summary>
/// R3-12: a tunnel with a name, a health check and a DSN. The forward itself is still
/// <see cref="TunnelSupervisor"/>'s detached `ssh -N`; this layer adds what a person or an agent
/// needs to use it: one name, one connection string, and one answer to "is it actually serving".
///
/// Honest limits: a TCP accept proves something is listening on the bind port, not that the far end
/// answers for the service — that is what an optional check command is for, and without one the
/// verdict says "tcp only". A remote (-R) forward has nothing local to probe, so it is reported as
/// unknown unless it has a check command.
/// </summary>
public static class ServiceTunnels
{
    public static string StorePath => Path.Combine(DataStore.RootPath, "services.json");

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    // ---------------------------------------------------------------- store

    public static List<ServiceDefinition> Load(string? path = null)
    {
        path ??= StorePath;
        var services = new List<ServiceDefinition>();
        if (!File.Exists(path)) return services;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document) return services;
            if (document["services"] is not JsonArray rows) return services;
            foreach (var row in rows.OfType<JsonObject>())
                services.Add(new ServiceDefinition
                {
                    Name = row["name"]?.GetValue<string>() ?? "",
                    SessionId = row["session"]?.GetValue<string>() ?? "",
                    Spec = row["spec"]?.GetValue<string>() ?? "",
                    Dsn = row["dsn"]?.GetValue<string>() ?? "",
                    CheckCommand = row["check"]?.GetValue<string>() ?? "",
                    Note = row["note"]?.GetValue<string>() ?? "",
                    Created = row["created"]?.GetValue<string>() ?? ""
                });
        }
        catch (Exception ex)
        {
            Log.Warn("services: " + ex.Message);
        }

        return services;
    }

    public static void Save(List<ServiceDefinition> services, string? path = null)
    {
        path ??= StorePath;
        var rows = new JsonArray();
        foreach (var service in services)
            rows.Add(new JsonObject
            {
                ["name"] = service.Name,
                ["session"] = service.SessionId,
                ["spec"] = service.Spec,
                ["dsn"] = service.Dsn,
                ["check"] = service.CheckCommand,
                ["note"] = service.Note,
                ["created"] = service.Created
            });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary,
            new JsonObject { ["version"] = 1, ["services"] = rows }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public static ServiceDefinition? Find(string name, string? path = null)
        => Load(path).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces a named service. The spec is parsed now, so a typo fails here.</summary>
    public static ServiceDefinition Add(string name, Session session, string spec, string? dsn = null,
        string? checkCommand = null, string? note = null, string? path = null)
    {
        name = name.Trim().ToLowerInvariant();
        if (!NamePattern.IsMatch(name))
            throw new InvalidOperationException("service names are [a-z0-9_-], 1..32 chars");

        var parsed = TunnelSupervisor.Parse(spec);          // throws TunnelException on a bad spec
        var services = Load(path);
        if (services.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("a service named '" + name + "' already exists — remove it first");

        var service = new ServiceDefinition
        {
            Name = name,
            SessionId = session.Id,
            Spec = spec.Trim(),
            Dsn = dsn?.Trim() ?? "",
            CheckCommand = checkCommand?.Trim() ?? "",
            Note = note?.Trim() ?? "",
            Created = DateTimeOffset.UtcNow.ToString("O")
        };

        // A DSN that names nothing this service knows about is a typo, not a template.
        ExpandDsn(service, session, parsed);

        services.Add(service);
        Save(services, path);
        EvidenceChain.Append("service", session, new JsonObject
        {
            ["action"] = "add",
            ["name"] = service.Name,
            ["spec"] = service.Spec,
            ["check"] = service.CheckCommand
        });
        return service;
    }

    public static bool Remove(string name, out string error, string? path = null)
    {
        error = "";
        var services = Load(path);
        var match = services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            error = services.Count == 0 ? "no services are defined"
                : "no service named '" + name + "' — defined: " + string.Join(", ", services.Select(s => s.Name));
            return false;
        }

        services.Remove(match);
        Save(services, path);
        EvidenceChain.Append("service", null, new JsonObject { ["action"] = "remove", ["name"] = match.Name });
        return true;
    }

    // ---------------------------------------------------------------- DSN

    /// <summary>
    /// Fills a DSN template. `{host}` is the bind address a local client uses (loopback for a
    /// wildcard bind), `{port}` the bind port, `{user}` the session's user, `{session}` its name,
    /// `{dest}`/`{destport}` the far end. An unknown token is refused rather than left in the string.
    /// </summary>
    public static string ExpandDsn(ServiceDefinition service, Session session, ForwardSpec spec)
    {
        var host = spec.BindAddress is "" or "0.0.0.0" or "::" or "localhost" ? "127.0.0.1" : spec.BindAddress;
        var template = string.IsNullOrWhiteSpace(service.Dsn) ? "tcp://{host}:{port}" : service.Dsn;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = host,
            ["port"] = spec.BindPort.ToString(),
            ["user"] = session.User,
            ["session"] = session.Name,
            ["id"] = session.Id,
            ["dest"] = spec.DestHost,
            ["destport"] = spec.DestPort.ToString()
        };

        var unknown = Regex.Matches(template, "\\{([a-z0-9_]+)\\}", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Where(token => !values.ContainsKey(token))
            .Distinct()
            .ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("the DSN names unknown token(s) " + string.Join(", ", unknown)
                                                + " — known: " + string.Join(", ", values.Keys));

        return Regex.Replace(template, "\\{([a-z0-9_]+)\\}",
            m => values[m.Groups[1].Value], RegexOptions.IgnoreCase);
    }

    // ---------------------------------------------------------------- starting and checking

    /// <summary>Starts the forward, waits for the bind port, then runs the health check.</summary>
    public static ServiceReport Up(ServiceDefinition service, Session session, int checkTimeoutSeconds = 20)
    {
        if (service.SessionId.Length > 0 && service.SessionId != session.Id)
            throw new InvalidOperationException("service '" + service.Name + "' is defined for session '"
                                                + service.SessionId + "', not '" + session.Id + "'");

        var spec = TunnelSupervisor.Parse(service.Spec);
        var tunnel = TunnelSupervisor.Start(session, service.Spec);
        var report = new ServiceReport
        {
            Name = service.Name,
            Spec = service.Spec,
            Dsn = ExpandDsn(service, session, spec),
            TunnelId = tunnel.Id,
            Pid = tunnel.Pid
        };

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, Math.Min(30, checkTimeoutSeconds)));
        while (DateTime.UtcNow < deadline)
        {
            var probe = Probe(spec);
            if (probe.Status == "up") break;
            Thread.Sleep(400);
        }

        var check = Check(service, session);
        report.Status = check.Status;
        report.Detail = check.Detail;
        report.Millis = check.Millis;
        report.CheckOutput = check.CheckOutput;

        EvidenceChain.Append("service", session, new JsonObject
        {
            ["action"] = "up",
            ["name"] = service.Name,
            ["tunnel"] = tunnel.Id,
            ["status"] = report.Status,
            ["check"] = service.CheckCommand
        });
        return report;
    }

    /// <summary>The health check: the bind port answers, and (when defined) the host command succeeds.</summary>
    public static ServiceReport Check(ServiceDefinition service, Session session)
    {
        var spec = TunnelSupervisor.Parse(service.Spec);
        var report = new ServiceReport
        {
            Name = service.Name,
            Spec = service.Spec,
            Dsn = ExpandDsn(service, session, spec)
        };

        if (spec.Kind == TunnelKind.Remote)
        {
            report.Status = "unknown";
            report.Detail = "a remote (-R) forward has nothing on this machine to probe; "
                            + (service.CheckCommand.Length == 0 ? "add a check command to have an answer" : "using the check command");
        }
        else
        {
            var probe = Probe(spec);
            report.Status = probe.Status;
            report.Detail = probe.Detail;
            report.Millis = probe.Millis;
            if (probe.Status != "up") return report;
        }

        if (service.CheckCommand.Length > 0)
        {
            var result = AgentCli.RunCommand(session, service.CheckCommand, 20);
            report.CheckOutput = (result.Stdout + result.Stderr).Trim();
            if (!result.Ok)
            {
                report.Status = "down";
                report.Detail = "the check command exited " + result.Exit + ": " + Trim(report.CheckOutput);
                return report;
            }
            report.Detail += "; check \"" + service.CheckCommand + "\" ok";
        }

        return report;
    }

    private static string Trim(string text)
        => text.Length <= 160 ? text : text.Substring(0, 160) + "…";

    /// <summary>Does the bind port accept a connection? A TCP accept is "something is listening".</summary>
    public static (string Status, string Detail, int Millis) Probe(ForwardSpec spec, int timeoutMs = 2500)
    {
        if (spec.BindPort == 0) return ("down", "the bind port is missing", 0);
        var host = spec.BindAddress is "" or "0.0.0.0" or "::" or "localhost" ? "127.0.0.1" : spec.BindAddress;
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, spec.BindPort);
            if (!connect.Wait(timeoutMs))
                return ("down", "no answer within " + timeoutMs + " ms on " + host + ":" + spec.BindPort, (int)watch.ElapsedMilliseconds);
            return ("up", "tcp " + host + ":" + spec.BindPort + " accepted", (int)watch.ElapsedMilliseconds);
        }
        catch (AggregateException ex)
        {
            var inner = ex.InnerException ?? ex;
            return ("down", host + ":" + spec.BindPort + " — " + Short(inner.Message), (int)watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return ("down", host + ":" + spec.BindPort + " — " + Short(ex.Message), (int)watch.ElapsedMilliseconds);
        }
    }

    private static string Short(string message)
        => message.Length <= 120 ? message : message.Substring(0, 120) + "…";

    // ---------------------------------------------------------------- reporting

    public static string ListText(bool json, string? path = null)
    {
        var services = Load(path);
        var tunnels = TunnelSupervisor.List();
        if (json)
            return JsonSerializer.Serialize(new
            {
                services = services.Select(s => new
                {
                    name = s.Name, session = s.SessionId, spec = s.Spec, dsn = s.Dsn,
                    check = s.CheckCommand, note = s.Note, created = s.Created,
                    running = tunnels.Any(t => t.Spec == s.Spec)
                })
            });

        if (services.Count == 0) return "no services defined (--service-add <name> <session> <spec>)";
        var lines = new List<string> { services.Count + " service(s)" };
        foreach (var service in services.OrderBy(s => s.Name))
        {
            var running = tunnels.FirstOrDefault(t => t.Spec == service.Spec);
            lines.Add(service.Name.PadRight(16) + service.SessionId.PadRight(12) + service.Spec.PadRight(34)
                      + (running is null ? "stopped" : "running " + running.Id)
                      + (service.CheckCommand.Length == 0 ? "" : "  check: " + service.CheckCommand));
        }
        return string.Join("\n", lines);
    }

    public static string ReportText(ServiceReport report)
        => report.Name + " " + report.Status.ToUpperInvariant()
           + (report.Millis > 0 ? " (" + report.Millis + " ms)" : "")
           + "\n  " + report.Detail
           + (report.TunnelId.Length > 0 ? "\n  tunnel " + report.TunnelId + " pid " + report.Pid : "")
           + "\n  dsn " + report.Dsn
           + (report.CheckOutput.Length > 0 ? "\n  output: " + report.CheckOutput : "");

    public static string ReportJson(ServiceReport report) => JsonSerializer.Serialize(new
    {
        name = report.Name, status = report.Status, detail = report.Detail, millis = report.Millis,
        tunnel = report.TunnelId, pid = report.Pid, dsn = report.Dsn, output = report.CheckOutput
    });
}
