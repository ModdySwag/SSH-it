using System.Net;
using System.Net.Sockets;

namespace SshIt.Services;

/// <summary>
/// X11 forwarding on Windows needs two things OpenSSH does not arrange for us: a local X
/// server to hand the forwarded connection to, and a DISPLAY value for the ssh child naming
/// it. Without either, `-X` connects fine and the first GUI program fails on the host.
/// </summary>
public static class X11Support
{
    public const int FirstDisplayPort = 6000;
    public const int LastDisplayPort = 6005;

    private static int? _cachedPort;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>The value to put in the child's DISPLAY, or null when nothing is reachable.</summary>
    public static string? ResolveDisplay() => DisplayFor(Environment.GetEnvironmentVariable("DISPLAY"), ListeningPort());

    /// <summary>
    /// Pure form of <see cref="ResolveDisplay"/>: an explicit DISPLAY always wins (the user may
    /// be running an X server somewhere else), otherwise the first listening port names it.
    /// </summary>
    public static string? DisplayFor(string? existing, int? listeningPort)
    {
        if (!string.IsNullOrWhiteSpace(existing)) return existing.Trim();
        if (listeningPort is null) return null;
        return "127.0.0.1:" + (listeningPort.Value - FirstDisplayPort);
    }

    /// <summary>First local port in 6000-6005 that accepts a connection; cached for 30 seconds.</summary>
    public static int? ListeningPort(bool refresh = false)
    {
        if (!refresh && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return _cachedPort;

        var probes = new List<Task<(int Port, bool Open)>>();
        for (var port = FirstDisplayPort; port <= LastDisplayPort; port++)
        {
            var candidate = port;
            probes.Add(Task.Run(() => (candidate, Probe(candidate))));
        }

        try { Task.WaitAll(probes.ToArray(), 500); } catch { }

        int? found = null;
        foreach (var probe in probes)
        {
            if (!probe.IsCompletedSuccessfully) continue;
            if (!probe.Result.Open) continue;
            if (found is null || probe.Result.Port < found) found = probe.Result.Port;
        }

        _cachedPort = found;
        _cachedAt = DateTime.UtcNow;
        return found;
    }

    public static bool Available => ResolveDisplay() is not null;

    /// <summary>One line for the session editor and the connection notice.</summary>
    public static string Describe()
    {
        var display = ResolveDisplay();
        if (display is not null) return "X11 will be sent to " + display + ".";

        return "No X server found on 127.0.0.1 (displays :0 to :" + (LastDisplayPort - FirstDisplayPort) +
               "). Install VcXsrv or X410, start it, then reconnect.";
    }

    private static bool Probe(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            return connect.Wait(120) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
