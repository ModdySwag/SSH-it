using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// A signed, append-only local record of what this client did: every command block, transfer and
/// tunnel becomes one JSONL row carrying the previous row's hash and a signature from this
/// machine's device key. Editing, reordering or removing a row breaks the chain at a named line,
/// which is what <see cref="Verify"/> reports.
///
/// Honest limits, printed wherever this is shown: it proves what this client SENT, not what the
/// host did with it, and it cannot detect truncation of the newest rows (nothing follows them) or
/// re-signing by someone who can read the device key. It is evidence, not a notary.
///
/// The signature is ECDSA P-256 over SHA-256. The roadmap said Ed25519; .NET 9 has no Ed25519, and
/// this project takes no third-party packages, so the row records its algorithm (`alg`) and the
/// public key it was signed with, and a verifier can accept either.
/// </summary>
public static class EvidenceChain
{
    public const string Algorithm = "ecdsa-p256-sha256";

    private static readonly object Gate = new();
    private static ECDsa? _key;
    private static string? _keyFile;

    public static string DirectoryPath => Path.Combine(DataStore.RootPath, "evidence");

    public static string ChainPath => Path.Combine(DirectoryPath, "chain.jsonl");

    public sealed class Verification
    {
        public bool Ok { get; set; }
        public int Count { get; set; }
        public string? FirstBreak { get; set; }
        public int BreakLine { get; set; }
        public int BreakSeq { get; set; }
        public string KeyFingerprint { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Limit { get; } =
            "Proves what this client sent, not what the host did. Truncation of the newest rows " +
            "cannot be detected, and anyone holding the device key can re-sign a rewritten chain.";
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Appends one row. Never throws into a caller: a failed append logs and returns, because an
    /// audit trail must not take the terminal down with it.
    /// </summary>
    public static void Append(string kind, Session? session, JsonObject data, string? path = null)
    {
        try
        {
            path ??= ChainPath;
            lock (Gate)
            {
                var key = KeyFor(path);
                if (key is null) return;

                var (seq, prev) = LastRow(path);
                var row = new JsonObject
                {
                    ["seq"] = seq + 1,
                    ["at"] = DateTime.UtcNow.ToString("O"),
                    ["kind"] = kind,
                    ["session"] = session?.Id ?? "",
                    ["host"] = session?.Host ?? "",
                    ["user"] = session?.User ?? "",
                    ["data"] = data.DeepClone(),
                    ["prev"] = prev
                };

                var payload = Payload(Signed(row));
                row["hash"] = Hash(payload);
                row["alg"] = Algorithm;
                row["pub"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                row["sig"] = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, row.ToJsonString() + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("evidence: append failed: " + ex.Message);
        }
    }

    /// <summary>The plain-language fingerprint a user can compare out of band.</summary>
    public static string KeyFingerprint(string? path = null)
    {
        try
        {
            var key = KeyFor(path ?? ChainPath, create: false);
            return key is null ? "" : Fingerprint(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        }
        catch { return ""; }
    }

    /// <summary>
    /// The device key's public half (base64 SPKI). A lease row is verified against THIS key, not
    /// against the key the row carries: a row that names its own key would let anyone who can write
    /// the file re-sign it.
    /// </summary>
    public static string DevicePublicKey()
    {
        try
        {
            var key = KeyFor(ChainPath, create: false);
            return key is null ? "" : Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch { return ""; }
    }

    /// <summary>Signs one canonical document with the device key. Null when the key is unavailable.</summary>
    public static (string PublicKey, string Signature)? Sign(string canonical)
    {
        try
        {
            var key = KeyFor(ChainPath);
            if (key is null) return null;
            return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256)));
        }
        catch (Exception ex)
        {
            Log.Warn("evidence: signing failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Verifies a signature over canonical text — the same math the chain uses per row.</summary>
    public static bool VerifyCanonical(string publicKey, string signature, string canonical)
        => VerifySignature(publicKey, signature, Encoding.UTF8.GetBytes(canonical));

    /// <summary>Sorted keys, no whitespace: the same bytes on every machine and every run.</summary>
    public static string CanonicalJson(JsonNode? node) => Canonical(node);

    // ---------------------------------------------------------------- verifying

    public static Verification Verify(string? path = null)
    {
        path ??= ChainPath;
        var result = new Verification { Chain = path };
        if (!File.Exists(path))
        {
            result.FirstBreak = "no evidence chain at " + path;
            return result;
        }

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex)
        {
            result.FirstBreak = "cannot read the chain: " + ex.Message;
            return result;
        }

        var expectedSeq = 1;
        var prevHash = "";
        string? pub = null;

        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            JsonObject row;
            try
            {
                row = JsonNode.Parse(lines[i]) as JsonObject
                      ?? throw new InvalidOperationException("the line is not a JSON object");
            }
            catch (Exception ex)
            {
                return Break(result, i + 1, expectedSeq, "line " + (i + 1) + " is not a row: " + ex.Message);
            }

            var seq = row["seq"]?.GetValue<int>() ?? -1;
            if (seq != expectedSeq)
                return Break(result, i + 1, seq,
                    $"sequence jumps: row {expectedSeq} was expected, the file holds {seq}. A row was removed, inserted or reordered.");

            var prev = row["prev"]?.GetValue<string>() ?? "";
            if (!string.Equals(prev, prevHash, StringComparison.OrdinalIgnoreCase))
                return Break(result, i + 1, seq,
                    "the previous-row hash does not match: a row was edited, removed or moved.");

            var rowPub = row["pub"]?.GetValue<string>() ?? "";
            if (pub is null) pub = rowPub;
            else if (!string.Equals(rowPub, pub, StringComparison.Ordinal))
                return Break(result, i + 1, seq, "the signing key changed mid-chain.");

            var hash = row["hash"]?.GetValue<string>() ?? "";
            var payload = Payload(Signed(row));
            if (!string.Equals(hash, Hash(payload), StringComparison.OrdinalIgnoreCase))
                return Break(result, i + 1, seq, "the row's hash does not match its content: the row was edited.");

            var signature = row["sig"]?.GetValue<string>() ?? "";
            if (!VerifySignature(rowPub, signature, payload))
                return Break(result, i + 1, seq, "the signature does not verify against the key in the row.");

            result.Count++;
            expectedSeq++;
            prevHash = hash;
        }

        result.KeyFingerprint = Fingerprint(pub);
        if (result.Count == 0)
        {
            result.FirstBreak = "the chain holds no rows.";
            return result;
        }

        result.Ok = true;
        return result;
    }

    private static Verification Break(Verification result, int line, int seq, string reason)
    {
        result.Ok = false;
        result.BreakLine = line;
        result.BreakSeq = seq;
        result.FirstBreak = $"row {seq} (line {line}): {reason}";
        return result;
    }

    // ---------------------------------------------------------------- export and listing

    /// <summary>One self-contained JSON file: the verification verdict, the key, and every row.</summary>
    public static string Export(string path, string? chain = null)
    {
        chain ??= ChainPath;
        var verdict = Verify(chain);
        var rows = new JsonArray();
        if (File.Exists(chain))
        {
            foreach (var line in File.ReadAllLines(chain))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { rows.Add(JsonNode.Parse(line)); }
                catch { rows.Add(line); }
            }
        }

        var bundle = new JsonObject
        {
            ["exported"] = DateTime.UtcNow.ToString("O"),
            ["chain"] = chain,
            ["rows"] = verdict.Count,
            ["ok"] = verdict.Ok,
            ["firstBreak"] = verdict.FirstBreak,
            ["key"] = verdict.KeyFingerprint,
            ["alg"] = Algorithm,
            ["limit"] = verdict.Limit,
            ["evidence"] = rows
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return verdict.Ok
            ? $"exported {verdict.Count} row(s) to {path}; the chain verifies"
            : $"exported {verdict.Count} row(s) to {path}; the chain is BROKEN: {verdict.FirstBreak}";
    }

    /// <summary>The newest rows, for a window or a tail command.</summary>
    public static List<JsonObject> Tail(int count, string? path = null)
    {
        path ??= ChainPath;
        var rows = new List<JsonObject>();
        if (!File.Exists(path)) return rows;
        var lines = File.ReadAllLines(path);
        for (var i = lines.Length - 1; i >= 0 && rows.Count < count; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                if (JsonNode.Parse(lines[i]) is JsonObject row) rows.Add(row);
            }
            catch { /* a damaged row is what Verify is for */ }
        }
        rows.Reverse();
        return rows;
    }

    // ---------------------------------------------------------------- row maths

    /// <summary>The fields a signature covers, copied out so neither side depends on key order.</summary>
    private static JsonObject Signed(JsonObject row) => new()
    {
        ["seq"] = row["seq"]?.DeepClone(),
        ["at"] = row["at"]?.DeepClone() ?? "",
        ["kind"] = row["kind"]?.DeepClone() ?? "",
        ["session"] = row["session"]?.DeepClone() ?? "",
        ["host"] = row["host"]?.DeepClone() ?? "",
        ["user"] = row["user"]?.DeepClone() ?? "",
        ["data"] = row["data"]?.DeepClone() ?? new JsonObject(),
        ["prev"] = row["prev"]?.DeepClone() ?? ""
    };

    private static byte[] Payload(JsonObject signed) =>
        Encoding.UTF8.GetBytes((signed["prev"]?.GetValue<string>() ?? "") + "\n" + Canonical(signed));

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Fingerprint(string? publicKey) =>
        string.IsNullOrEmpty(publicKey)
            ? ""
            : "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(publicKey))).TrimEnd('=');

    private static bool VerifySignature(string publicKey, string signature, byte[] payload)
    {
        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>Sorted keys, no whitespace: the same bytes on every machine and every run.</summary>
    private static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteCanonical(node, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject obj:
                text.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append(JsonSerializer.Serialize(pair.Key));
                    text.Append(':');
                    WriteCanonical(pair.Value, text);
                }
                text.Append('}');
                break;
            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    WriteCanonical(array[i], text);
                }
                text.Append(']');
                break;
            default:
                text.Append(node.ToJsonString());
                break;
        }
    }

    private static (int Seq, string Hash) LastRow(string path)
    {
        if (!File.Exists(path)) return (0, "");
        var lines = File.ReadAllLines(path);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                if (JsonNode.Parse(lines[i]) is JsonObject row)
                    return (row["seq"]?.GetValue<int>() ?? 0, row["hash"]?.GetValue<string>() ?? "");
            }
            catch { /* fall through to the row above */ }
        }
        return (0, "");
    }

    // ---------------------------------------------------------------- the device key

    private static ECDsa? KeyFor(string path, bool create = true)
    {
        var keyFile = path + ".key";
        lock (Gate)
        {
            if (_key is not null && _keyFile == keyFile) return _key;

            var key = LoadKey(keyFile);
            if (key is null && create) key = CreateKey(keyFile);
            _key = key;
            _keyFile = keyFile;
            return key;
        }
    }

    /// <summary>
    /// The private key is DPAPI-protected for the current Windows user: another account cannot
    /// read it even with the file. If DPAPI is unavailable the append is skipped rather than
    /// writing an unprotected key.
    /// </summary>
    private static ECDsa? LoadKey(string keyFile)
    {
        try
        {
            if (!File.Exists(keyFile)) return null;
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
            var pkcs8 = Unprotect(protectedBytes);
            if (pkcs8 is null) return null;
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        catch (Exception ex)
        {
            Log.Warn("evidence: device key unreadable: " + ex.Message);
            return null;
        }
    }

    private static ECDsa? CreateKey(string keyFile)
    {
        try
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = key.ExportPkcs8PrivateKey();
            var guarded = Protect(pkcs8);
            if (guarded is null)
            {
                Log.Warn("evidence: DPAPI unavailable, refusing to store an unprotected device key");
                key.Dispose();
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.WriteAllText(keyFile, Convert.ToBase64String(guarded), new UTF8Encoding(false));
            return key;
        }
        catch (Exception ex)
        {
            Log.Warn("evidence: device key could not be created: " + ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- DPAPI

    /// <summary>DPAPI-protects bytes for the current user (used by the approver keys as well as the chain key).</summary>
    public static byte[]? ProtectBytes(byte[] plain, string description) => Protect(plain);

    /// <summary>Reverses <see cref="ProtectBytes"/>; null when DPAPI refuses.</summary>
    public static byte[]? UnprotectBytes(byte[] guarded) => Unprotect(guarded);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[]? Protect(byte[] plain)
    {
        var input = new DataBlob { Size = plain.Length, Data = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.Data, plain.Length);
            if (!CryptProtectData(ref input, "SSH-it evidence key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    0, out var output)) return null;
            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    private static byte[]? Unprotect(byte[] guarded)
    {
        var input = new DataBlob { Size = guarded.Length, Data = Marshal.AllocHGlobal(guarded.Length) };
        try
        {
            Marshal.Copy(guarded, 0, input.Data, guarded.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    0, out var output)) return null;
            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
