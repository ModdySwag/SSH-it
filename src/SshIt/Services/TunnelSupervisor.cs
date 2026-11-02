using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>Raised when a tunnel cannot be described or started.</summary>
public sealed class TunnelException : Exception
{
    public TunnelException(string message) : base(message) { }
}

/// <summary>A parsed forward specification: L, R or D in bind:port[:host:port] form.</summary>
public sealed record ForwardSpec(TunnelKind Kind, string BindAddress, int BindPort, string DestHost, int DestPort)
{
    public string Forward => Kind switch
    {
        TunnelKind.Local => $"-L {BindAddress}:{BindPort}:{DestHost}:{DestPort}",
        TunnelKind.Remote => $"-R {BindAddress}:{BindPort}:{DestHost}:{DestPort}",
        _ => $"-D {BindAddress}:{BindPort}"
    };

    public string Summary => Kind switch
    {
        TunnelKind.Local => $"L  {BindAddress}:{BindPort} -> {DestHost}:{DestPort}",
        TunnelKind.Remote => $"R  {BindAddress}:{BindPort} <- {DestHost}:{DestPort}",
        _ => $"D  {BindAddress}:{BindPort} (SOCKS5)"
    };
}

/// <summary>
/// Keeps `ssh -N` forwards alive after the command that started them exits.
///
/// The agent surface is one-shot by design, so a tunnel is not owned by a live parent: it is
/// started detached and recorded in a small state file, and later commands re-attach to it by
/// pid. Liveness means the pid still exists AND its start time still matches, so a recycled
/// pid is never mistaken for the tunnel. There is no auto-restart — a tunnel that dies stays
/// dead until something starts it again, and the list says so rather than pretending.
/// </summary>
public static class TunnelSupervisor
{
    public sealed class Entry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
        public string SessionId { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string Spec { get; set; } = "";
        public string Forward { get; set; } = "";
        public int Pid { get; set; }
        public long StartedAt { get; set; }
        public string Created { get; set; } = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
    }

    /// <summary>Tests point this at a scratch file; production uses the store directory.</summary>
    internal static string StatePathOverride { get; set; } = "";

    internal static string StatePath
    {
        get
        {
            if (StatePathOverride.Length > 0) return StatePathOverride;

            var exeDir = AppContext.BaseDirectory;
            var root = File.Exists(Path.Combine(exeDir, "SSH-it.portable"))
                ? Path.Combine(exeDir, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SSH-it");
            return Path.Combine(root, "tunnels-agent.json");
        }
    }

    // ---------------------------------------------------------------- specification

    /// <summary>
    /// Parses `L:127.0.0.1:8080:localhost:80`, `R:0.0.0.0:9000:localhost:22` or
    /// `D:127.0.0.1:1080`. The kind prefix is case-insensitive and optional (local by default).
    /// </summary>
    public static ForwardSpec Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new TunnelException("a forward spec is required");

        var text = spec.Trim();
        var kind = TunnelKind.Local;
        if (text.Length > 1 && text[1] == ':')
        {
            kind = char.ToUpperInvariant(text[0]) switch
            {
                'L' => TunnelKind.Local,
                'R' => TunnelKind.Remote,
                'D' => TunnelKind.Dynamic,
                _ => throw new TunnelException($"'{text[0]}' is not a forward kind; use L, R or D")
            };
            text = text[2..];
        }

        var parts = text.Split(':');
        if (kind == TunnelKind.Dynamic)
        {
            if (parts.Length != 2) throw new TunnelException("a dynamic forward is D:bind:port");
            return new ForwardSpec(kind, parts[0], Port(parts[1]), "", 0);
        }

        if (parts.Length != 4) throw new TunnelException("a forward is L:bind:port:host:port (or R:...)");
        return new ForwardSpec(kind, parts[0], Port(parts[1]), parts[2], Port(parts[3]));
    }

    private static int Port(string text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port <= 65535
            ? port
            : throw new TunnelException($"'{text}' is not a port");

    // ---------------------------------------------------------------- lifecycle

    public static Entry Start(Session session, string spec)
    {
        var parsed = Parse(spec);
        if (parsed.BindPort == 0) throw new TunnelException("the bind port is missing");

        // A port that already accepts connections is somebody else's listener: the readiness probe
        // would connect to it and report a healthy tunnel while ssh exits with "bind: Address
        // already in use". Refuse before spawning anything.
        if (parsed.Kind != TunnelKind.Remote && IsLoopback(parsed.BindAddress) && PortAccepts(parsed.BindAddress, parsed.BindPort))
            throw new TunnelException($"local port {parsed.BindPort} is already in use — nothing was started");

        var entry = new Entry
        {
            SessionId = session.Id,
            SessionName = session.DisplayTarget,
            Spec = spec.Trim(),
            Forward = parsed.Forward
        };

        var args = $"-N -o ExitOnForwardFailure=yes {parsed.Forward} {SshLauncher.BuildBatchArguments(session)}";
        var info = new ProcessStartInfo(SshLauncher.SshExe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        var secret = CredentialVault.GetSessionSecret(session.Id);
        if (!string.IsNullOrEmpty(secret))
        {
            info.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? "";
            info.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            info.Environment["CONDUIT_ASKPASS"] = CredentialVault.StoreAskPass(session.Id, secret);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new TunnelException("ssh.exe could not be started");
        }
        catch (TunnelException) { throw; }
        catch (Exception ex) { throw new TunnelException(ex.Message); }

        entry.Pid = process.Id;
        try { entry.StartedAt = process.StartTime.ToUniversalTime().Ticks; }
        catch { entry.StartedAt = 0; }

        // ssh reports a bind failure on stderr and exits; keep the first lines so the command
        // that started the tunnel can say why instead of leaving a mystery process behind.
        var errors = new StringBuilder();
        try
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (errors) { if (errors.Length < 800) errors.AppendLine(e.Data.Trim()); }
            };
            process.BeginErrorReadLine();
        }
        catch { /* the pipe can close with the parent */ }

        if (!WaitUntilReady(parsed, process, TimeSpan.FromSeconds(7)))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }

            string detail;
            lock (errors) detail = errors.ToString().Trim();
            throw new TunnelException(detail.Length > 0
                ? detail
                : $"ssh exited with code {(process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?")} before the forward was ready");
        }

        var entries = Read();
        entries.Add(entry);
        Write(entries);
        EvidenceChain.Append("tunnel", session, new JsonObject
        {
            ["spec"] = entry.Spec,
            ["forward"] = parsed.Forward,
            ["pid"] = entry.Pid
        });
        return entry;
    }

    /// <summary>
    /// A local or dynamic forward is ready when its port accepts a connection; a remote
    /// forward has nothing local to probe, so it is ready when ssh is still alive after the
    /// grace period and reported no error.
    /// </summary>
    private static bool WaitUntilReady(ForwardSpec spec, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var probe = spec.Kind != TunnelKind.Remote && IsLoopback(spec.BindAddress);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return false;
            if (!probe) { Thread.Sleep(400); if (!process.HasExited) return true; continue; }

            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddressFor(spec.BindAddress), spec.BindPort);
                if (connect.Wait(400) && client.Connected) return true;
            }
            catch { /* not listening yet */ }

            Thread.Sleep(250);
        }

        return !process.HasExited && !probe;
    }

    /// <summary>True when something already accepts a connection on that loopback port.</summary>
    private static bool PortAccepts(string address, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddressFor(address), port);
            return connect.Wait(300) && client.Connected;
        }
        catch { return false; }
    }

    private static bool IsLoopback(string address)
        => address.Length == 0 || address == "127.0.0.1" || address == "localhost" || address == "::1";

    private static System.Net.IPAddress IPAddressFor(string address)
        => address is "" or "localhost" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Parse(address);

    public static List<Entry> List()
    {
        var entries = Read();
        var live = new List<Entry>();
        foreach (var entry in entries) if (IsAlive(entry)) live.Add(entry);
        if (live.Count != entries.Count) Write(live);
        return live;
    }

    public static Entry? Find(string idOrSpec)
        => List().FirstOrDefault(entry =>
            entry.Id.StartsWith(idOrSpec, StringComparison.OrdinalIgnoreCase)
            || entry.Spec.Equals(idOrSpec, StringComparison.OrdinalIgnoreCase)
            || entry.Forward.Equals(idOrSpec, StringComparison.OrdinalIgnoreCase));

    public static bool Stop(string idOrSpec)
    {
        // An empty selector starts every id and every spec: `Id.StartsWith("")` is true for the
        // first row, so `--tunnel-down ""` killed whichever tunnel happened to be listed first.
        if (string.IsNullOrWhiteSpace(idOrSpec)) return false;

        var entries = Read();
        var entry = entries.FirstOrDefault(candidate =>
            candidate.Id.StartsWith(idOrSpec, StringComparison.OrdinalIgnoreCase)
            || candidate.Spec.Equals(idOrSpec, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;

        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }

        entries.Remove(entry);
        Write(entries);
        return true;
    }

    private static bool IsAlive(Entry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            if (process.HasExited) return false;
            return entry.StartedAt == 0 || process.StartTime.ToUniversalTime().Ticks == entry.StartedAt;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- state file

    private static List<Entry> Read()
    {
        try
        {
            if (!File.Exists(StatePath)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StatePath)) ?? new List<Entry>();
        }
        catch (Exception ex)
        {
            Log.Warn("tunnels: " + ex.Message);
            return new List<Entry>();
        }
    }

    private static void Write(List<Entry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("tunnels: " + ex.Message);
        }
    }
}
