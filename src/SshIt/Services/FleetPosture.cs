using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>One side's KEXINIT proposal: what an SSH peer is willing to speak.</summary>
public sealed class AlgoOffer
{
    public List<string> Kex { get; set; } = new();
    public List<string> HostKey { get; set; } = new();
    public List<string> CipherCtos { get; set; } = new();
    public List<string> CipherStoc { get; set; } = new();
    public List<string> MacCtos { get; set; } = new();
    public List<string> MacStoc { get; set; } = new();

    public bool Any => Kex.Count + HostKey.Count + CipherCtos.Count + MacCtos.Count > 0;

    /// <summary>True when this side proposed the algorithm in any of its lists.</summary>
    public bool Offers(string algorithm) =>
        Kex.Contains(algorithm, StringComparer.OrdinalIgnoreCase) ||
        HostKey.Contains(algorithm, StringComparer.OrdinalIgnoreCase) ||
        CipherCtos.Contains(algorithm, StringComparer.OrdinalIgnoreCase) ||
        CipherStoc.Contains(algorithm, StringComparer.OrdinalIgnoreCase) ||
        MacCtos.Contains(algorithm, StringComparer.OrdinalIgnoreCase) ||
        MacStoc.Contains(algorithm, StringComparer.OrdinalIgnoreCase);
}

public sealed class HostKeySize
{
    public string Type { get; init; } = "";
    public int Bits { get; init; }
}

public sealed class PostureFinding
{
    public string Severity { get; init; } = "info";   // high | medium | low | info
    public string Area { get; init; } = "";
    public string Item { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Fix { get; init; } = "";
}

/// <summary>Everything learned about one saved host in a single read-only pass.</summary>
public sealed class HostPosture
{
    public string SessionName { get; init; } = "";
    public string Target { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;

    public bool Reachable { get; set; }
    public bool Authenticated { get; set; }
    public string? FailureStage { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Banner: SSH protocol version and the server software string as reported.</summary>
    public string? Protocol { get; set; }
    public string? Software { get; set; }

    /// <summary>What the two ends actually negotiated (server-to-client direction shown).</summary>
    public string? Kex { get; set; }
    public string? HostKeyAlgo { get; set; }
    public string? CipherStoc { get; set; }
    public string? MacStoc { get; set; }

    public AlgoOffer Offered { get; set; } = new();
    public AlgoOffer ClientOffer { get; set; } = new();
    public List<string> Auth { get; set; } = new();
    public Dictionary<string, string> SshdConfig { get; set; } = new();
    public string? SshdNote { get; set; }
    public List<HostKeyVerdict> HostKeyVerdicts { get; set; } = new();
    public List<HostKeySize> OfferedHostKeys { get; set; } = new();
    public List<string> CertAuthorities { get; set; } = new();
    public List<PostureFinding> Findings { get; set; } = new();

    public int Issues => Findings.Count(f => f.Severity != "info");
    public int Notes => Findings.Count(f => f.Severity == "info");

    /// <summary>
    /// A sort key, not a rating: high 10, medium 4, low 1, info 0. The weights exist so the
    /// worst host rises to the top of a table; they are not a CVSS score and the UI says so.
    /// </summary>
    public int Points => Findings.Sum(f => f.Severity switch
    {
        "high" => 10,
        "medium" => 4,
        "low" => 1,
        _ => 0
    });

    /// <summary>Highest severity that is actually a problem; "clean" when only notes remain.</summary>
    public string Worst => Findings.Any(f => f.Severity == "high") ? "high"
        : Findings.Any(f => f.Severity == "medium") ? "medium"
        : Findings.Any(f => f.Severity == "low") ? "low"
        : "clean";
}

/// <summary>
/// Fleet posture: one read-only pass over every saved SSH host, using the two probes the app
/// already owns — <see cref="SshDiagnostics"/> for the handshake (including the full KEXINIT
/// proposals both sides sent) and <see cref="HostKeys"/> for known_hosts and offered keys.
///
/// The scoring is deliberately derived from what the server actually advertised, not from a
/// vulnerability database: an algorithm a server does not offer cannot be negotiated, and one it
/// does offer is reachable by any client old enough to ask for it. Every finding names the
/// algorithm and prints the sshd_config line that removes it. `sshd -T` is read where the probe
/// authenticated and the account has root or NOPASSWD sudo; when it is not available the report
/// says so instead of guessing at root login.
/// </summary>
public static class FleetPosture
{
    // ---------------------------------------------------------------- scanning

    /// <summary>Sessions that can be posture-scanned: real SSH targets, not local or serial tabs.</summary>
    public static List<Session> Scannable(IEnumerable<Session> sessions) =>
        sessions.Where(s => !s.IsLocal
                            && string.Equals(s.Kind, "Ssh", StringComparison.OrdinalIgnoreCase)
                            && (!string.IsNullOrWhiteSpace(s.Host) || !string.IsNullOrWhiteSpace(s.ConfigAlias)))
                .ToList();

    public static async Task<List<HostPosture>> ScanAsync(
        IEnumerable<Session> sessions,
        IProgress<string>? progress,
        IProgress<HostPosture>? completed,
        CancellationToken token,
        int concurrency = 4)
    {
        var list = Scannable(sessions);
        var results = new List<HostPosture>();
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var index = 0;

        var tasks = list.Select(async session =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var number = Interlocked.Increment(ref index);
                progress?.Report($"Probing {session.DisplayTarget} ({number} of {list.Count})…");
                var posture = await ScanHostAsync(session, token).ConfigureAwait(false);
                lock (results) results.Add(posture);
                completed?.Report(posture);
                return posture;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* every host returns its own row; a stop is not an error */ }

        return results
            .OrderByDescending(r => r.Points)
            .ThenBy(r => r.SessionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<HostPosture> ScanHostAsync(Session session, CancellationToken token)
    {
        var started = Environment.TickCount64;
        var host = new HostPosture
        {
            SessionName = session.Name,
            Target = session.DisplayTarget,
            Host = session.Host,
            Port = session.Port
        };

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(token);
        cap.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            var report = await SshDiagnostics.RunAsync(session, cap.Token).ConfigureAwait(false);

            // ssh -G resolved the real target, which is the only name known_hosts and keyscan
            // can use when the session connects through a Host alias.
            var effectiveHost = report.Effective.FirstOrDefault(r => r.Key == "hostname").Value;
            var effectivePort = report.Effective.FirstOrDefault(r => r.Key == "port").Value;
            if (!string.IsNullOrWhiteSpace(effectiveHost)) host = Clone(host, effectiveHost!, host.Port);
            if (int.TryParse(effectivePort, out var port) && port > 0) host = Clone(host, host.Host, port);

            host.Reachable = report.Authenticated || report.Steps.Any(s => s.Stage == "TCP" && s.State == "ok"
                && s.Detail.StartsWith("Connection established", StringComparison.OrdinalIgnoreCase));
            host.Authenticated = report.Authenticated;
            host.FailureStage = report.FailureStage;
            host.Error = report.ProbeError ?? report.FailureText;

            var banner = ParseBanner(report.ProbeLog);
            host.Protocol = banner.Protocol;
            host.Software = banner.Software;

            var negotiated = ParseNegotiated(report.ProbeLog);
            host.Kex = negotiated.Kex;
            host.HostKeyAlgo = negotiated.HostKey;
            host.CipherStoc = negotiated.Cipher;
            host.MacStoc = negotiated.Mac;

            var proposals = ParseProposals(report.ProbeLog);
            host.Offered = proposals.Server;
            host.ClientOffer = proposals.Client;
            host.Auth = ParseAuth(report.ProbeLog);

            if (host.Reachable && host.Offered.Any)
            {
                var probeTarget = new Session { Host = host.Host, Port = host.Port };
                var keys = await Task.Run(() => HostKeys.Inspect(probeTarget), cap.Token).ConfigureAwait(false);
                host.HostKeyVerdicts = keys.Verdicts;
                host.OfferedHostKeys = keys.Current
                    .Select(k => new HostKeySize { Type = k.KeyType, Bits = KeyBits(k.KeyType, k.Blob) })
                    .Where(k => k.Bits > 0)
                    .ToList();
                host.CertAuthorities = HostKeys.CertAuthorities(host.Host, host.Port);
            }

            if (host.Authenticated)
            {
                var (values, note) = await ReadSshdConfigAsync(session, cap.Token).ConfigureAwait(false);
                host.SshdConfig = values;
                host.SshdNote = note;
            }
            else
            {
                host.SshdNote = "not read: the probe did not authenticate (BatchMode has no password to offer)";
            }
        }
        catch (OperationCanceledException)
        {
            host.Error = token.IsCancellationRequested ? "cancelled" : "timed out after 45 s";
        }
        catch (Exception ex)
        {
            host.Error = ex.Message;
        }

        host.DurationMs = Environment.TickCount64 - started;
        host.Findings = Score(host);
        return host;
    }

    /// <summary>Records are not used because the fields are set from several sources; copy instead.</summary>
    private static HostPosture Clone(HostPosture source, string host, int port) => new()
    {
        SessionName = source.SessionName,
        Target = source.Target,
        Host = host,
        Port = port,
        Reachable = source.Reachable,
        Authenticated = source.Authenticated,
        FailureStage = source.FailureStage,
        Error = source.Error,
        Protocol = source.Protocol,
        Software = source.Software,
        Kex = source.Kex,
        HostKeyAlgo = source.HostKeyAlgo,
        CipherStoc = source.CipherStoc,
        MacStoc = source.MacStoc,
        Offered = source.Offered,
        ClientOffer = source.ClientOffer,
        Auth = source.Auth
    };

    // ---------------------------------------------------------------- sshd -T

    /// <summary>
    /// The server's effective sshd configuration, when the host lets us read it. `sshd -T` needs
    /// root (it loads the host keys), so this tries the plain command and then passwordless sudo;
    /// anything else is reported as "not available" rather than inferred.
    /// </summary>
    public static async Task<(Dictionary<string, string> Values, string? Note)> ReadSshdConfigAsync(
        Session session, CancellationToken token)
    {
        const string command = "sshd -T 2>/dev/null || sudo -n sshd -T 2>/dev/null";
        // -T matters: without it ssh wants a pseudo-terminal and its own dup() of stdin/stdout
        // fails on Windows when the caller has no console, which reads as an empty result.
        // -n keeps ssh from duplicating the caller's stdin at all: a one-shot probe never reads it.
        var args = "-n -T -o BatchMode=yes -o ConnectTimeout=8 "
                   + SshLauncher.BuildBatchArguments(session) + " "
                   + SshLauncher.QuoteArgument(command);

        var info = new ProcessStartInfo(SshLauncher.SshExe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(token);
        cap.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var process = Process.Start(info);
            if (process is null) return (new Dictionary<string, string>(), "ssh did not start");
            var stdout = process.StandardOutput.ReadToEndAsync(cap.Token);
            var stderr = process.StandardError.ReadToEndAsync(cap.Token);
            await process.WaitForExitAsync(cap.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);

            var values = ParseSshdConfig(text);
            if (values.ContainsKey("permitrootlogin")) return (values, null);
            Log.Warn("posture: sshd -T gave no configuration. args=" + args + " out=" + Shorten(text));
            return (values, "sshd -T produced no configuration (it needs root or NOPASSWD sudo on the host)");
        }
        catch (OperationCanceledException)
        {
            return (new Dictionary<string, string>(), token.IsCancellationRequested
                ? "not read: scan stopped"
                : "sshd -T timed out");
        }
        catch (Exception ex)
        {
            return (new Dictionary<string, string>(), "sshd -T failed: " + ex.Message);
        }
    }

    /// <summary>First 300 characters of a probe's output, for the log only.</summary>
    private static string Shorten(string text)
    {
        var flat = (text ?? "").Replace('\r', ' ').Replace('\n', '|');
        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }

    /// <summary>Parses `sshd -T` output: lowercase "key value" lines, repeats joined by commas.</summary>
    public static Dictionary<string, string> ParseSshdConfig(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var key = line[..space].Trim().ToLowerInvariant();
            if (!key.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;   // skip paths and error prose
            var value = line[(space + 1)..].Trim();
            values[key] = values.TryGetValue(key, out var existing) ? existing + ", " + value : value;
        }
        return values;
    }

    // ---------------------------------------------------------------- parsing one handshake log

    /// <summary>Both KEXINIT proposals from a `ssh -vvv` log: what we offered and what the server offered.</summary>
    public static (AlgoOffer Client, AlgoOffer Server) ParseProposals(string log)
    {
        var client = new AlgoOffer();
        var server = new AlgoOffer();
        AlgoOffer? target = null;

        foreach (var raw in (log ?? "").Split('\n'))
        {
            var line = Strip(raw);
            if (line.Length == 0) continue;

            if (line.EndsWith("KEXINIT proposal", StringComparison.OrdinalIgnoreCase))
            {
                target = line.StartsWith("local client", StringComparison.OrdinalIgnoreCase) ? client
                    : line.StartsWith("peer server", StringComparison.OrdinalIgnoreCase) ? server
                    : null;
                continue;
            }
            if (target is null) continue;
            if (line.StartsWith("first_kex_follows", StringComparison.OrdinalIgnoreCase)) { target = null; continue; }

            var (key, value) = SplitField(line);
            if (key is null) continue;
            var list = SplitList(value);
            if (list.Count == 0) continue;

            switch (key)
            {
                case "kex algorithms":
                    if (target.Kex.Count == 0) target.Kex = list.Where(a => !IsPseudoAlgorithm(a)).ToList();
                    break;
                case "host key algorithms":
                    if (target.HostKey.Count == 0) target.HostKey = list;
                    break;
                case "ciphers ctos":
                    if (target.CipherCtos.Count == 0) target.CipherCtos = list;
                    break;
                case "ciphers stoc":
                    if (target.CipherStoc.Count == 0) target.CipherStoc = list;
                    break;
                case "macs ctos":
                    if (target.MacCtos.Count == 0) target.MacCtos = list;
                    break;
                case "macs stoc":
                    if (target.MacStoc.Count == 0) target.MacStoc = list;
                    break;
            }
        }

        return (client, server);
    }

    /// <summary>Drops "debug2: " (and friends) so one matcher covers debug1/2/3 lines.</summary>
    private static string Strip(string raw)
    {
        var text = raw.TrimEnd('\r').Trim();
        if (text.StartsWith("debug", StringComparison.Ordinal))
        {
            var at = text.IndexOf(": ", StringComparison.Ordinal);
            if (at > 0 && at <= 12) text = text[(at + 2)..].Trim();
        }
        return text;
    }

    private static (string? Key, string Value) SplitField(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return (null, "");
        return (line[..colon].Trim().ToLowerInvariant(), line[(colon + 1)..].Trim());
    }

    private static List<string> SplitList(string value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>ext-info-c/s and kex-strict-* are negotiation markers, not key exchanges.</summary>
    private static bool IsPseudoAlgorithm(string name) =>
        name.StartsWith("ext-info", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("kex-strict", StringComparison.OrdinalIgnoreCase);

    /// <summary>"remote software version OpenSSH_10.0p2 Debian-7+deb13u4" and the protocol version.</summary>
    public static (string? Protocol, string? Software) ParseBanner(string log)
    {
        foreach (var raw in (log ?? "").Split('\n'))
        {
            var line = Strip(raw);
            var match = Regex.Match(line, @"Remote protocol version (\S+),\s*remote software version (.+)$",
                RegexOptions.IgnoreCase);
            if (match.Success) return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
        }
        return (null, null);
    }

    /// <summary>The algorithms that were actually chosen, from the "kex:" summary lines.</summary>
    public static (string? Kex, string? HostKey, string? Cipher, string? Mac) ParseNegotiated(string log)
    {
        string? kex = null, hostKey = null, cipher = null, mac = null;
        var cipherLine = new Regex(@"server->client\s+cipher:\s*(\S+)\s+MAC:\s*(\S+)", RegexOptions.IgnoreCase);

        foreach (var raw in (log ?? "").Split('\n'))
        {
            var line = Strip(raw);
            if (line.Contains("kex: algorithm:", StringComparison.OrdinalIgnoreCase))
                kex ??= After(line, "kex: algorithm:");
            else if (line.Contains("kex: host key algorithm:", StringComparison.OrdinalIgnoreCase))
                hostKey ??= After(line, "kex: host key algorithm:");
            else if (line.Contains("server->client cipher:", StringComparison.OrdinalIgnoreCase))
            {
                var match = cipherLine.Match(line);
                if (match.Success)
                {
                    cipher ??= match.Groups[1].Value;
                    mac ??= match.Groups[2].Value;
                }
            }
        }
        return (kex, hostKey, cipher, mac);
    }

    private static string After(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? line.Trim() : line[(at + marker.Length)..].Trim();
    }

    /// <summary>The first "Authentications that can continue" list: the widest set the server offers.</summary>
    public static List<string> ParseAuth(string log)
    {
        foreach (var raw in (log ?? "").Split('\n'))
        {
            var line = Strip(raw);
            const string marker = "Authentications that can continue:";
            var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var list = SplitList(line[(at + marker.Length)..]);
            if (list.Count > 0) return list;
        }
        return new List<string>();
    }

    // ---------------------------------------------------------------- host key sizes

    /// <summary>
    /// Bit length of a host key from its known_hosts/keyscan base64 blob. RSA is the one that
    /// matters here: a 1024-bit key and a 2048-bit key are advertised by the same algorithm name.
    /// Returns 0 when the blob cannot be read.
    /// </summary>
    public static int KeyBits(string keyType, string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return 0;
        byte[] blob;
        try { blob = Convert.FromBase64String(base64); }
        catch { return 0; }
        if (blob.Length < 6) return 0;

        var reader = new BlobReader(blob);
        var type = reader.ReadString();
        if (type.Length == 0) return 0;

        if (type is "ssh-rsa" or "rsa-sha2-256" or "rsa-sha2-512")
        {
            reader.ReadString();                        // public exponent
            var modulus = reader.ReadBytes();           // mpint: may carry leading zero bytes
            if (modulus.Length == 0) return 0;

            var index = 0;
            while (index < modulus.Length - 1 && modulus[index] == 0) index++;
            var significant = modulus.Length - index;
            var top = modulus[index];
            return (significant - 1) * 8 + (top == 0 ? 0 : BitOperations.Log2(top) + 1);
        }

        if (type == "ssh-dss") return 1024;
        if (type == "ssh-ed25519" || type.StartsWith("sk-ssh-ed25519", StringComparison.Ordinal)) return 256;
        if (type.StartsWith("ecdsa-sha2-nistp", StringComparison.Ordinal) &&
            int.TryParse(type["ecdsa-sha2-nistp".Length..], out var curve)) return curve;
        return 0;
    }

    private sealed class BlobReader
    {
        private readonly byte[] _blob;
        private int _pos;

        public BlobReader(byte[] blob) => _blob = blob;

        public string ReadString()
        {
            var bytes = ReadBytes();
            return bytes.Length == 0 ? "" : Encoding.ASCII.GetString(bytes);
        }

        public byte[] ReadBytes()
        {
            if (_pos + 4 > _blob.Length) return Array.Empty<byte>();
            var length = (_blob[_pos] << 24) | (_blob[_pos + 1] << 16) | (_blob[_pos + 2] << 8) | _blob[_pos + 3];
            _pos += 4;
            if (length <= 0 || _pos + length > _blob.Length) return Array.Empty<byte>();
            var slice = new byte[length];
            Array.Copy(_blob, _pos, slice, 0, length);
            _pos += length;
            return slice;
        }
    }

    // ---------------------------------------------------------------- scoring

    // The weak tables are keyed by the exact algorithm name OpenSSH prints, so a typo in a rule
    // can only ever miss a finding; it can never invent one. Severities follow the usual split:
    // high = broken or removed from OpenSSH's own defaults, medium = still default-reachable but
    // no longer recommended, low = legacy for compliance rather than attack.
    private static readonly Dictionary<string, (string Severity, string Why)> WeakKex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["diffie-hellman-group1-sha1"] = ("high",
            "1024-bit fixed group with SHA-1; precomputation (Logjam) makes it cheap to break even now."),
        ["diffie-hellman-group-exchange-sha1"] = ("high",
            "SHA-1 exchange over a group whose size the server chooses, so it can be as weak as group1."),
        ["rsa1024-sha1"] = ("high",
            "1024-bit RSA key exchange with SHA-1."),
        ["diffie-hellman-group14-sha1"] = ("medium",
            "2048-bit group but SHA-1 as the exchange hash; OpenSSH no longer proposes it by default.")
    };

    private static readonly Dictionary<string, (string Severity, string Why)> WeakHostKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh-dss"] = ("high",
            "1024-bit DSA; removed from OpenSSH 7.0 because discrete-log attacks on this group size are practical."),
        ["ssh-dss-cert-v01@openssh.com"] = ("high",
            "Certificate of a 1024-bit DSA key; the same weakness as ssh-dss."),
        ["ssh-rsa"] = ("medium",
            "SHA-1 RSA signature. Disabled in OpenSSH 8.8 and later, so only an older client can negotiate it."),
        ["ssh-rsa-cert-v01@openssh.com"] = ("medium",
            "Certificate signed with SHA-1 RSA; disabled in current OpenSSH.")
    };

    private static readonly Dictionary<string, (string Severity, string Why)> WeakCipher = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3des-cbc"] = ("high",
            "64-bit block cipher: SWEET32 recovers plaintext from long-lived sessions, and it is slow besides."),
        ["arcfour"] = ("high", "RC4: biased keystream, and removed from every current SSH implementation."),
        ["arcfour128"] = ("high", "RC4 with a 128-bit key: the keystream bias remains."),
        ["arcfour256"] = ("high", "RC4 with a 256-bit key: the keystream bias remains."),
        ["blowfish-cbc"] = ("high", "64-bit block cipher with CBC; SWEET32 applies."),
        ["cast128-cbc"] = ("high", "64-bit block cipher with CBC; SWEET32 applies."),
        ["none"] = ("high", "No encryption at all."),
        ["aes128-cbc"] = ("medium", "CBC without integrity: the SSH CBC padding oracle (CVE-2008-5161) applies."),
        ["aes192-cbc"] = ("medium", "CBC without integrity: the SSH CBC padding oracle applies."),
        ["aes256-cbc"] = ("medium", "CBC without integrity: the SSH CBC padding oracle applies."),
        ["rijndael-cbc@lysator.liu.se"] = ("medium", "AES-CBC under an old name; the same padding oracle applies.")
    };

    private static readonly Dictionary<string, (string Severity, string Why)> WeakMac = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hmac-md5"] = ("high", "MD5-based MAC; collisions are trivial and it is long removed from defaults."),
        ["hmac-md5-96"] = ("high", "MD5-based MAC with a truncated tag."),
        ["hmac-md5-etm@openssh.com"] = ("high", "MD5-based encrypt-then-MAC; the hash is broken whatever the order."),
        ["hmac-md5-96-etm@openssh.com"] = ("high", "MD5-based encrypt-then-MAC with a truncated tag."),
        ["hmac-sha1"] = ("low", "SHA-1 based. Still in OpenSSH's default list for compatibility, but no longer recommended."),
        ["hmac-sha1-96"] = ("low", "SHA-1 with a truncated 96-bit tag."),
        ["hmac-sha1-etm@openssh.com"] = ("low", "SHA-1 encrypt-then-MAC; the order is right, the hash is dated."),
        ["hmac-sha1-96-etm@openssh.com"] = ("low", "SHA-1 encrypt-then-MAC with a truncated tag."),
        ["umac-64@openssh.com"] = ("low", "64-bit authentication tag; a forgery needs 2^64 work, which is no longer a wall."),
        ["umac-64-etm@openssh.com"] = ("low", "64-bit encrypt-then-MAC tag.")
    };

    private const string FixKex =
        "On the server (OpenSSH): KexAlgorithms curve25519-sha256,curve25519-sha256@libssh.org,"
        + "ecdh-sha2-nistp256,ecdh-sha2-nistp384,ecdh-sha2-nistp521,"
        + "diffie-hellman-group16-sha512,diffie-hellman-group18-sha512";

    private const string FixCipher =
        "On the server (OpenSSH): Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,"
        + "aes128-gcm@openssh.com,aes256-ctr,aes192-ctr,aes128-ctr";

    private const string FixMac =
        "On the server (OpenSSH): MACs hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm@openssh.com,"
        + "umac-128-etm@openssh.com";

    private const string FixHostKey =
        "Prefer an Ed25519 host key: ssh-keygen -t ed25519 -f /etc/ssh/ssh_host_ed25519_key, "
        + "add `HostKey /etc/ssh/ssh_host_ed25519_key`, then remove the weak key from HostKey.";

    /// <summary>Scores one host. Pure: everything it needs is already in the report.</summary>
    public static List<PostureFinding> Score(HostPosture host)
    {
        var findings = new List<PostureFinding>();

        void Add(string severity, string area, string item, string detail, string fix = "") =>
            findings.Add(new PostureFinding
            {
                Severity = severity, Area = area, Item = item, Detail = detail, Fix = fix
            });

        if (!host.Reachable)
        {
            Add("info", "Reachability", host.FailureStage ?? "no answer",
                host.Error ?? "The probe did not reach an SSH banner, so no algorithm or server setting could be read.",
                "Session > Diagnose connection on this host names the stage that stopped.");
            return findings;
        }

        if (!host.Offered.Any)
        {
            Add("info", "Handshake", host.FailureStage ?? "incomplete",
                host.Error ?? "TCP opened but no key exchange happened, so there is no algorithm list to score.",
                "Session > Diagnose connection on this host walks the handshake.");
            return findings;
        }

        // What this client itself is willing to offer, used only to qualify a finding honestly.
        var clientAny = host.ClientOffer.Any;
        string ClientNote(AlgoOffer mine, string item) =>
            clientAny && !mine.Offers(item) ? " This client does not offer it, so only an older client can negotiate it." : "";

        foreach (var item in host.Offered.Kex)
        {
            if (!WeakKex.TryGetValue(item, out var rule)) continue;
            Add(rule.Severity, "Key exchange", item, rule.Why + ClientNote(host.ClientOffer, item), FixKex);
        }

        foreach (var item in host.Offered.HostKey)
        {
            if (!WeakHostKey.TryGetValue(item, out var rule)) continue;
            Add(rule.Severity, "Host key", item, rule.Why + ClientNote(host.ClientOffer, item), FixHostKey);
        }

        foreach (var item in host.Offered.CipherCtos.Concat(host.Offered.CipherStoc).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!WeakCipher.TryGetValue(item, out var rule)) continue;
            Add(rule.Severity, "Cipher", item, rule.Why + ClientNote(host.ClientOffer, item), FixCipher);
        }

        foreach (var item in host.Offered.MacCtos.Concat(host.Offered.MacStoc).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!WeakMac.TryGetValue(item, out var rule)) continue;
            Add(rule.Severity, "MAC", item,
                rule.Why + " Only consulted when a non-AEAD cipher is negotiated." + ClientNote(host.ClientOffer, item),
                FixMac);
        }

        // Host keys that are modern by name but small by measurement (RSA 1024 is the common one).
        foreach (var key in host.OfferedHostKeys)
        {
            if (!key.Type.StartsWith("ssh-rsa", StringComparison.OrdinalIgnoreCase) &&
                !key.Type.StartsWith("rsa-sha2", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Bits >= 2048 || key.Bits == 0) continue;
            Add("high", "Host key", $"{key.Type} {key.Bits}-bit",
                $"The host key is {key.Bits} bits. RSA below 2048 bits is considered factorable with nation-state resources "
                + "and is rejected by current OpenSSH defaults (2048-bit minimum).",
                "Generate an Ed25519 host key, or at least rsa-sha2 with 3072 bits or more.");
        }

        // Authentication: what the server offered this connection, then what its config says.
        var auth = host.Auth.Select(a => a.Trim().ToLowerInvariant()).ToList();
        var configPassword = host.SshdConfig.TryGetValue("passwordauthentication", out var pw) ? pw.ToLowerInvariant() : null;

        if (auth.Count > 0 && !auth.Contains("publickey"))
            Add("high", "Authentication", "no publickey",
                "The server did not offer public-key authentication, so keys cannot be used on this host at all.",
                "On the server: PubkeyAuthentication yes");

        if (auth.Contains("password"))
            Add("medium", "Authentication", "password",
                "Password logins are accepted, so every account on this host is exposed to guessing and credential stuffing"
                + (configPassword is null ? "." : $" (sshd -T reports passwordauthentication {configPassword})."),
                "On the server: PasswordAuthentication no   # confirm keys work for every account that needs in first");

        if (!auth.Contains("password") && configPassword == "yes")
            Add("low", "Authentication", "password (not offered here)",
                "sshd -T reports PasswordAuthentication yes, but this connection was not offered it. A Match block is the usual reason: "
                + "the setting applies to someone else, and still exposes them.",
                "Scope the Match block deliberately, or turn password authentication off globally.");

        if (auth.Contains("keyboard-interactive"))
            Add("low", "Authentication", "keyboard-interactive",
                "Interactive/PAM logins are accepted. Often this is 2FA, but it can also be a password prompt a script cannot answer.",
                "KbdInteractiveAuthentication no, unless PAM provides the second factor.");

        // Server configuration, only where `sshd -T` actually answered.
        if (host.SshdConfig.TryGetValue("permitrootlogin", out var rootLogin))
        {
            var value = rootLogin.ToLowerInvariant();
            if (value is "yes")
                Add("high", "Server config", "PermitRootLogin yes",
                    "root may log in directly. One guessed or reused credential is full control of the host.",
                    "On the server: PermitRootLogin prohibit-password   # or no");
            else if (value is "forced-commands-only")
                Add("info", "Server config", "PermitRootLogin forced-commands-only",
                    "root logins are limited to forced commands; key-based access is otherwise refused.", "");
        }

        if (host.SshdConfig.TryGetValue("permitemptypasswords", out var empty) &&
            empty.Equals("yes", StringComparison.OrdinalIgnoreCase))
            Add("high", "Server config", "PermitEmptyPasswords yes",
                "Accounts with a blank password can log in without one.",
                "On the server: PermitEmptyPasswords no");

        if (host.SshdConfig.TryGetValue("maxauthtries", out var tries) &&
            int.TryParse(tries, out var attempts) && attempts > 6)
            Add("low", "Server config", $"MaxAuthTries {attempts}",
                "The server allows more authentication attempts per connection than the default, which widens a password guess loop.",
                "On the server: MaxAuthTries 6");

        // Banner: reported version only. Distributions backport fixes, so this never claims a CVE.
        if (host.Protocol is not null && (host.Protocol.StartsWith("1.", StringComparison.Ordinal) || host.Protocol == "1.99"))
            Add("high", "Server version", "SSH protocol " + host.Protocol,
                "The server still speaks SSH protocol 1, which has known design flaws.",
                "On the server: Protocol 2");

        var version = Regex.Match(host.Software ?? "", @"OpenSSH[_-](\d+)\.(\d+)");
        if (version.Success)
        {
            var major = int.Parse(version.Groups[1].Value);
            var minor = int.Parse(version.Groups[2].Value);
            if (major < 7)
                Add("medium", "Server version", host.Software!,
                    "The banner reports an OpenSSH older than 7.0, whose defaults still accept diffie-hellman-group1-sha1 and DSA host keys.",
                    "Upgrade the server. Check the package version rather than the banner: distributions backport fixes.");
            else if (major < 9)
                Add("low", "Server version", host.Software!,
                    "The banner reports an OpenSSH older than 9.0. Its default proposals still include SHA-1 algorithms "
                    + "(ssh-rsa, hmac-sha1); the package may well be backported, so treat this as a prompt to check.",
                    "Upgrade or confirm the distribution's backport level.");
        }

        // Trust: what known_hosts holds versus what the host offers now.
        foreach (var verdict in host.HostKeyVerdicts)
        {
            if (verdict.Status == "changed")
                Add("high", "Host key trust", verdict.KeyType,
                    $"known_hosts stores {verdict.Stored} but the host now offers {verdict.Current}. "
                    + "A reinstall and an interception look identical at this point.",
                    "Verify the fingerprint out of band, then `ssh-keygen -R <host>` and reconnect.");
            else if (verdict.Status == "new")
                Add("low", "Host key trust", verdict.KeyType,
                    "The host offers this key type and known_hosts has no entry for it yet.",
                    "Normal on first contact: confirm the fingerprint in the Host keys window before accepting.");
            else if (verdict.Status == "missing")
                Add("low", "Host key trust", verdict.KeyType,
                    "known_hosts stores this key type but the host no longer offers it. Usually a rotation.",
                    "Remove the stale line (`ssh-keygen -R <host>`) once the new key has been verified.");
        }

        if (host.Offered.HostKey.Any(k => k.Contains("cert-v01", StringComparison.OrdinalIgnoreCase)))
            Add("info", "Host key", "host certificate",
                "The server presents a host certificate rather than a bare key. Trust comes from a @cert-authority line in known_hosts, "
                + "so a rotated key does not appear as a mismatch.",
                "Keep the CA line under review; it is the one entry that authenticates every host it signs.");

        if (host.CertAuthorities.Count > 0)
            Add("info", "Host key trust", "@cert-authority",
                "known_hosts trusts keys signed by a CA entry for this host: " + string.Join(", ", host.CertAuthorities),
                "");

        return findings
            .OrderByDescending(f => Rank(f.Severity))
            .ThenBy(f => f.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Rank(string severity) => severity switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    // ---------------------------------------------------------------- export

    public static string ToJson(IEnumerable<HostPosture> hosts) => JsonSerializer.Serialize(
        hosts.Select(h => new
        {
            session = h.SessionName,
            target = h.Target,
            host = h.Host,
            port = h.Port,
            reachable = h.Reachable,
            authenticated = h.Authenticated,
            protocol = h.Protocol,
            server = h.Software,
            negotiated = new { kex = h.Kex, hostKey = h.HostKeyAlgo, cipher = h.CipherStoc, mac = h.MacStoc },
            auth = h.Auth,
            offered = new
            {
                kex = h.Offered.Kex,
                hostKey = h.Offered.HostKey,
                ciphers = h.Offered.CipherCtos.Distinct().ToList(),
                macs = h.Offered.MacCtos.Distinct().ToList()
            },
            clientOffered = new
            {
                kex = h.ClientOffer.Kex,
                hostKey = h.ClientOffer.HostKey,
                ciphers = h.ClientOffer.CipherCtos.Distinct().ToList(),
                macs = h.ClientOffer.MacCtos.Distinct().ToList()
            },
            sshd = h.SshdConfig.Count == 0 ? null : h.SshdConfig,
            sshdNote = h.SshdNote,
            hostKeys = h.HostKeyVerdicts.Select(v => new
            {
                type = v.KeyType, status = v.Status, stored = v.Stored, current = v.Current
            }),
            findings = h.Findings.Select(f => new
            {
                severity = f.Severity, area = f.Area, item = f.Item, detail = f.Detail, fix = f.Fix
            }),
            worst = h.Worst,
            points = h.Points,
            error = h.Error,
            ms = h.DurationMs
        }), new JsonSerializerOptions { WriteIndented = true });

    public static string ToText(IEnumerable<HostPosture> hosts)
    {
        var text = new StringBuilder();
        foreach (var h in hosts)
        {
            text.AppendLine($"{h.SessionName}  {h.Target}  {h.Software ?? "?"}  worst={h.Worst} points={h.Points}"
                            + (h.Error is null ? "" : $"  [{h.Error}]"));
            foreach (var f in h.Findings)
                text.AppendLine($"  {f.Severity,-6} {f.Area,-16} {f.Item,-32} {f.Detail}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>One row per finding, plus one row per clean host, so nothing disappears in Excel.</summary>
    public static string ToCsv(IEnumerable<HostPosture> hosts)
    {
        var text = new StringBuilder();
        text.AppendLine("Host,Port,Session,Server,Worst,Points,Reachable,Area,Severity,Item,What it means,Fix");
        foreach (var h in hosts)
        {
            if (h.Findings.Count == 0)
            {
                text.AppendLine(string.Join(',', new[]
                {
                    Csv(h.Host), Csv(h.Port.ToString()), Csv(h.SessionName), Csv(h.Software ?? ""),
                    Csv("clean"), Csv("0"), Csv(h.Reachable ? "yes" : "no"),
                    Csv(""), Csv(""), Csv("no findings"), Csv(""), Csv("")
                }));
                continue;
            }
            foreach (var f in h.Findings)
            {
                text.AppendLine(string.Join(',', new[]
                {
                    Csv(h.Host), Csv(h.Port.ToString()), Csv(h.SessionName), Csv(h.Software ?? ""),
                    Csv(h.Worst), Csv(h.Points.ToString()), Csv(h.Reachable ? "yes" : "no"),
                    Csv(f.Area), Csv(f.Severity), Csv(f.Item), Csv(f.Detail), Csv(f.Fix)
                }));
            }
        }
        return text.ToString();
    }

    private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    public static string ToMarkdown(IEnumerable<HostPosture> hosts)
    {
        var list = hosts.ToList();
        var text = new StringBuilder();
        text.AppendLine("# Fleet posture — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        text.AppendLine();
        text.AppendLine($"{list.Count} host(s) scanned: "
                        + $"{list.Count(h => h.Worst == "clean")} clean, "
                        + $"{list.Count(h => h.Worst != "clean")} with findings, "
                        + $"{list.Count(h => !h.Reachable)} unreachable. "
                        + "Points: high 10, medium 4, low 1 — a sort key, not a rating.");
        text.AppendLine();
        text.AppendLine("| Host | Server | KEX | Host key | Cipher | MAC | Auth | Worst | Points |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var h in list)
        {
            text.AppendLine("| " + string.Join(" | ", new[]
            {
                Cell(h.SessionName), Cell(h.Software ?? (h.Reachable ? "?" : "unreachable")),
                Cell(h.Kex ?? "-"), Cell(h.HostKeyAlgo ?? "-"), Cell(h.CipherStoc ?? "-"),
                Cell(h.MacStoc ?? "-"), Cell(h.Auth.Count == 0 ? "-" : string.Join(", ", h.Auth)),
                Cell(h.Worst), h.Points.ToString()
            }) + " |");
        }

        foreach (var h in list)
        {
            text.AppendLine();
            text.AppendLine($"## {h.SessionName} ({h.Target}:{h.Port})");
            text.AppendLine();
            text.AppendLine($"- Server: {h.Software ?? "not reported"}"
                            + (h.Protocol is null ? "" : $" (protocol {h.Protocol})"));
            text.AppendLine($"- Negotiated: {h.Kex ?? "-"} · {h.HostKeyAlgo ?? "-"} · {h.CipherStoc ?? "-"} · {h.MacStoc ?? "-"}");
            text.AppendLine($"- Authentication offered: {(h.Auth.Count == 0 ? "-" : string.Join(", ", h.Auth))}");
            text.AppendLine($"- Verdict: {h.Worst}, {h.Points} point(s), {h.DurationMs} ms"
                            + (h.Error is null ? "" : $" — {h.Error}"));
            if (h.SshdConfig.Count > 0)
            {
                var keys = new[] { "permitrootlogin", "passwordauthentication", "kbdinteractiveauthentication", "permitemptypasswords", "maxauthtries" };
                text.AppendLine("- sshd -T: " + string.Join(", ", keys
                    .Where(k => h.SshdConfig.ContainsKey(k))
                    .Select(k => $"{k} {h.SshdConfig[k]}")));
            }
            else if (h.SshdNote is not null)
            {
                text.AppendLine("- sshd -T: " + h.SshdNote);
            }

            if (h.Findings.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("No findings.");
                continue;
            }

            foreach (var f in h.Findings)
            {
                text.AppendLine();
                text.AppendLine($"- **{f.Severity} · {f.Area} · {f.Item}** — {f.Detail}");
                if (f.Fix.Length > 0) text.AppendLine($"  `{f.Fix}`");
            }
        }

        return text.ToString();
    }

    /// <summary>A table cell cannot hold a pipe or a newline; both would break the row.</summary>
    private static string Cell(string value) =>
        (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    // ---------------------------------------------------------------- small helpers used by the UI

    public static string Summary(IEnumerable<HostPosture> hosts)
    {
        var list = hosts.ToList();
        if (list.Count == 0) return "No hosts scanned yet.";
        var worst = list.Select(h => h.Worst).OrderByDescending(Rank).First();
        var points = list.Sum(h => h.Points);
        return $"{list.Count} host(s) · {list.Count(h => h.Worst == "clean")} clean · "
               + $"{list.Count(h => h.Worst != "clean")} with findings · "
               + $"{list.Count(h => !h.Reachable)} unreachable · worst {worst} · {points} point(s)";
    }
}
