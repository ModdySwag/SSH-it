using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One address the sweep tried. Open is a TCP fact, not a guess: the socket connected.</summary>
public sealed class ScanHit
{
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public bool Open { get; init; }

    /// <summary>The first line the service sent, when it sent one. Empty is honest: many ports stay silent.</summary>
    public string Banner { get; init; } = "";

    /// <summary>SSH, HTTP, FTP/SMTP or open. Derived from the banner only — never from the port number.</summary>
    public string Service { get; init; } = "";

    /// <summary>Filled by the optional keyscan pass, for SSH hits only.</summary>
    public string Fingerprint { get; set; } = "";

    public int Milliseconds { get; init; }

    /// <summary>Why a closed address did not answer: refused, no answer, unreachable, cancelled.</summary>
    public string Note { get; init; } = "";

    public bool IsSsh => Open && Service == "SSH";

    /// <summary>Table text: an open port that said nothing is still an open port, and says so.</summary>
    public string BannerText => Banner.Length > 0 ? Banner : "(no banner)";

    public string FingerprintText => Fingerprint;
}

/// <summary>
/// A bounded TCP sweep for SSH hosts. Deliberately not a port scanner and not a ping sweep:
/// one port per run, TCP connect only (ICMP needs elevation and is filtered on most networks),
/// a hard address cap, and nothing written anywhere until the user adds a result as a session.
/// </summary>
public static class NetworkScan
{
    public const int DefaultPort = 22;
    public const int DefaultTimeoutMs = 400;
    public const int DefaultConcurrency = 96;

    /// <summary>Enough for a /20. A /16 is 65,536 addresses and is refused, not silently truncated.</summary>
    public const int MaxAddresses = 4096;

    private const int BannerTimeoutMs = 700;

    /// <summary>
    /// Reads one line: a single address, an IPv4 range (192.168.1.10-192.168.1.60 or
    /// 192.168.1.10-60), or an IPv4 CIDR block. Network and broadcast are skipped for /30 and
    /// wider. IPv6 is accepted as a single address only — a /64 is not a scan, it is a mistake.
    /// </summary>
    public static bool TryParseTargets(string? text, out List<IPAddress> addresses, out string error)
    {
        addresses = new List<IPAddress>();
        error = "";
        var spec = (text ?? "").Trim();
        if (spec.Length == 0)
        {
            error = "Enter an address, a range or a CIDR block (for example 192.168.1.0/24).";
            return false;
        }

        if (spec.Contains('/')) return TryParseCidr(spec, addresses, out error);
        if (spec.Contains('-')) return TryParseRange(spec, addresses, out error);

        if (!IPAddress.TryParse(spec, out var single))
        {
            error = $"'{spec}' is not an IP address, a range or a CIDR block.";
            return false;
        }
        addresses.Add(single);
        return true;
    }

    private static bool TryParseCidr(string spec, List<IPAddress> addresses, out string error)
    {
        error = "";
        var slash = spec.IndexOf('/');
        if (!IPAddress.TryParse(spec[..slash].Trim(), out var baseAddress) ||
            baseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"'{spec}' is not an IPv4 CIDR block. IPv6 is accepted as a single address only.";
            return false;
        }
        if (!int.TryParse(spec[(slash + 1)..].Trim(), out var prefix) || prefix is < 0 or > 32)
        {
            error = "A CIDR prefix must be a whole number from 0 to 32.";
            return false;
        }

        var count = prefix == 0 ? 1UL << 32 : 1UL << (32 - prefix);
        if (count > MaxAddresses)
        {
            error = $"/{prefix} is {count:N0} addresses; the cap is {MaxAddresses:N0}. Narrow the range.";
            return false;
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var first = ToUInt(baseAddress) & mask;
        var last = first + (uint)count - 1;

        // A /31 and /32 have no network or broadcast address to skip; wider blocks do.
        var from = prefix <= 30 ? first + 1 : first;
        var to = prefix <= 30 ? last - 1 : last;
        for (var value = from; ; value++)
        {
            addresses.Add(FromUInt(value));
            if (value == to) break;
        }
        return true;
    }

    private static bool TryParseRange(string spec, List<IPAddress> addresses, out string error)
    {
        error = "";
        var dash = spec.IndexOf('-');
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        if (!IPAddress.TryParse(left, out var start) || start.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"'{left}' is not an IPv4 address. Ranges are IPv4 only; give one IPv6 address instead.";
            return false;
        }

        IPAddress end;
        if (right.Contains('.'))
        {
            if (!IPAddress.TryParse(right, out var parsedEnd) || parsedEnd.AddressFamily != AddressFamily.InterNetwork)
            {
                error = $"'{right}' is not an IPv4 address.";
                return false;
            }
            end = parsedEnd;
        }
        else
        {
            // 192.168.1.10-60 keeps the first three octets.
            if (!int.TryParse(right, out var lastOctet) || lastOctet is < 0 or > 255)
            {
                error = $"'{right}' is not a last-octet number from 0 to 255.";
                return false;
            }
            var bytes = start.GetAddressBytes();
            bytes[3] = (byte)lastOctet;
            end = new IPAddress(bytes);
        }

        var from = ToUInt(start);
        var to = ToUInt(end);
        if (to < from)
        {
            error = "The range ends before it starts.";
            return false;
        }
        var count = (ulong)(to - from) + 1;
        if (count > MaxAddresses)
        {
            error = $"That range is {count:N0} addresses; the cap is {MaxAddresses:N0}. Narrow it.";
            return false;
        }
        for (var value = from; ; value++)
        {
            addresses.Add(FromUInt(value));
            if (value == to) break;
        }
        return true;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt(uint value)
        => new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    /// <summary>
    /// TCP-connects every address on one port, at most <paramref name="concurrency"/> at a time.
    /// Progress reports the number finished, not the number open. Cancelling keeps the partial list.
    /// </summary>
    public static async Task<List<ScanHit>> SweepAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        int timeoutMs,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken token,
        IProgress<ScanHit>? found = null)
    {
        port = port is > 0 and <= 65535 ? port : DefaultPort;
        timeoutMs = Math.Clamp(timeoutMs, 100, 10_000);
        concurrency = Math.Clamp(concurrency, 1, 512);

        var results = new ScanHit?[addresses.Count];
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var done = 0;

        var tasks = addresses.Select(async (address, index) =>
        {
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new ScanHit { Address = address.ToString(), Port = port, Note = "cancelled" };
                return;
            }

            try
            {
                var hit = await ProbeAsync(address, port, timeoutMs, token).ConfigureAwait(false);
                results[index] = hit;
                if (hit.Open) found?.Report(hit);
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(hit => hit is not null).Select(hit => hit!).ToList();
    }

    private static async Task<ScanHit> ProbeAsync(IPAddress address, int port, int timeoutMs, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var client = new TcpClient(address.AddressFamily);
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(token);
        clock.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(address, port, clock.Token).ConfigureAwait(false);
            watch.Stop();
            var banner = await ReadBannerAsync(client, token).ConfigureAwait(false);
            return new ScanHit
            {
                Address = address.ToString(),
                Port = port,
                Open = true,
                Banner = banner,
                Service = ClassifyBanner(banner),
                Milliseconds = (int)watch.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ScanHit { Address = address.ToString(), Port = port, Note = "cancelled", Milliseconds = (int)watch.ElapsedMilliseconds };
        }
        catch (OperationCanceledException)
        {
            return new ScanHit { Address = address.ToString(), Port = port, Note = "no answer", Milliseconds = (int)watch.ElapsedMilliseconds };
        }
        catch (SocketException ex)
        {
            return new ScanHit
            {
                Address = address.ToString(),
                Port = port,
                Note = ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "refused",
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable => "unreachable",
                    SocketError.TimedOut => "no answer",
                    _ => "error: " + ex.SocketErrorCode
                },
                Milliseconds = (int)watch.ElapsedMilliseconds
            };
        }
    }

    private static async Task<string> ReadBannerAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[512];
            using var clock = CancellationTokenSource.CreateLinkedTokenSource(token);
            clock.CancelAfter(BannerTimeoutMs);
            var read = await stream.ReadAsync(buffer, clock.Token).ConfigureAwait(false);
            if (read <= 0) return "";
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var line = text.Split('\n')[0].TrimEnd('\r').Trim();
            var clean = new string(line.Where(c => !char.IsControl(c)).ToArray());
            return clean.Length > 200 ? clean[..200] : clean;
        }
        catch
        {
            // A service that says nothing, or says it slowly, is still an open port.
            return "";
        }
    }

    /// <summary>The banner names the service; the port number alone never does.</summary>
    public static string ClassifyBanner(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return "open";
        if (banner.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase)) return "SSH";
        if (banner.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return "HTTP";
        if (banner.StartsWith("220", StringComparison.Ordinal)) return "FTP/SMTP";
        return "other";
    }

    /// <summary>
    /// Reads the offered host key for every open SSH hit, through the same bounded ssh-keyscan
    /// the posture scan uses. Slow by nature (one process per host), so it runs after the sweep
    /// and only for the few that answered.
    /// </summary>
    public static async Task FillFingerprintsAsync(
        IEnumerable<ScanHit> hits,
        int timeoutSeconds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken token)
    {
        var ssh = hits.Where(hit => hit.IsSsh).ToList();
        if (ssh.Count == 0) return;
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 32), Math.Clamp(concurrency, 1, 32));
        var done = 0;

        var tasks = ssh.Select(async hit =>
        {
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var keys = await Task.Run(() => HostKeys.Scan(hit.Address, hit.Port, timeoutSeconds), token)
                    .ConfigureAwait(false);
                var preferred = keys.FirstOrDefault(key => key.KeyType.StartsWith("ssh-ed25519", StringComparison.Ordinal))
                                ?? keys.FirstOrDefault(key => key.KeyType.StartsWith("ecdsa-", StringComparison.Ordinal))
                                ?? keys.FirstOrDefault();
                hit.Fingerprint = preferred?.Fingerprint ?? "";
            }
            catch (OperationCanceledException)
            {
                // Partial fingerprints are fine: the sweep result is already on screen.
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Sessions for the chosen hits, skipping any host and port already in the list. Nothing is
    /// saved here — the caller writes the store once, so a cancelled dialog leaves no trace.
    /// </summary>
    public static List<Session> SessionsFor(IEnumerable<ScanHit> hits, IEnumerable<Session> existing, out int duplicates)
    {
        var known = new HashSet<string>(existing.Select(session => Key(session.Host, session.Port)), StringComparer.OrdinalIgnoreCase);
        var created = new List<Session>();
        duplicates = 0;
        foreach (var hit in hits.Where(hit => hit.Open))
        {
            if (!known.Add(Key(hit.Address, hit.Port)))
            {
                duplicates++;
                continue;
            }
            created.Add(new Session { Name = hit.Address, Host = hit.Address, Port = hit.Port });
        }
        return created;
    }

    private static string Key(string host, int port) => (host ?? "").Trim().ToLowerInvariant() + ":" + port;

    /// <summary>
    /// A starting point for the dialog: the first live IPv4 interface whose mask is a sensible
    /// LAN (/22 to /30). Empty when there is none — the box stays blank rather than guessing.
    /// Nothing is scanned until the user presses Scan.
    /// </summary>
    public static string SuggestLocalBlock()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = unicast.IPv4Mask;
                    if (mask is null) continue;
                    var prefix = MaskToPrefix(mask);
                    if (prefix is < 22 or > 30) continue;
                    var network = ToUInt(unicast.Address) & ToUInt(mask);
                    return $"{FromUInt(network)}/{prefix}";
                }
            }
        }
        catch
        {
            // An interface list that cannot be read is not an error worth showing.
        }
        return "";
    }

    private static int MaskToPrefix(IPAddress mask)
    {
        var bits = ToUInt(mask);
        var prefix = 0;
        for (var i = 31; i >= 0; i--)
        {
            if (((bits >> i) & 1) == 0) break;
            prefix++;
        }
        var expected = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return bits == expected ? prefix : 0;
    }
}
