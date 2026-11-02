using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Services;

public sealed class SftpEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public long Size { get; init; }
    public uint? Permissions { get; init; }
    public DateTime? Modified { get; init; }

    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string PermissionsText => Permissions is null ? "" : FormatPermissions(Permissions.Value);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    /// <summary>A row announces its file name to a screen reader, not the type name.</summary>
    public override string ToString() => Name;

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string FormatPermissions(uint mode)
    {
        var builder = new StringBuilder();
        builder.Append((mode & 0x4000) != 0 ? 'd' : (mode & 0xA000) == 0xA000 ? 'l' : '-');
        const string rwx = "rwxrwxrwx";
        for (var i = 0; i < 9; i++)
            builder.Append((mode & (1u << (8 - i))) != 0 ? rwx[i] : '-');
        return builder.ToString();
    }
}

/// <summary>
/// Minimal SFTP v3 client speaking the protocol over <c>ssh -s … sftp</c>.
/// Using the OpenSSH subsystem means agent keys, ProxyJump and ssh_config all
/// work exactly as they do for a terminal session — one auth story, no third-party
/// SSH stack.
/// </summary>
public sealed class SftpClient : IDisposable
{
    /// <summary>
    /// Where a resumed transfer starts: the end of the shorter file, or 0 when there is nothing to
    /// gain (equal sizes, an empty file, or a local file longer than the remote one — in which case
    /// a fresh transfer is the only safe answer).
    /// </summary>
    public static long ResumeOffset(long localLength, long remoteSize) =>
        localLength > 0 && remoteSize > 0 && localLength < remoteSize ? localLength : 0;

    /// <summary>One decision a mirror makes about one remote entry.</summary>
    public sealed class MirrorItem
    {
        public string Action { get; init; } = "download";   // download | skip | create-dir
        public string RemotePath { get; init; } = "";
        public string LocalPath { get; init; } = "";
        public long Size { get; init; }
    }

    /// <summary>
    /// What a mirror does with one entry: create a directory, skip an identical file, or download.
    /// Pure so the policy is testable without a server; size is the only signal compared, because
    /// mtime on a Windows copy is not reliable enough to mean "unchanged".
    /// </summary>
    public static MirrorItem PlanMirror(SftpEntry entry, string localPath, bool localExists, long localSize)
    {
        if (entry.IsDirectory)
            return new MirrorItem { Action = "create-dir", RemotePath = entry.FullPath, LocalPath = localPath };
        if (localExists && localSize == entry.Size)
            return new MirrorItem { Action = "skip", RemotePath = entry.FullPath, LocalPath = localPath, Size = entry.Size };
        return new MirrorItem { Action = "download", RemotePath = entry.FullPath, LocalPath = localPath, Size = entry.Size };
    }

    /// <summary>
    /// One line describing a mirror plan: how many files would be fetched and how much data,
    /// how many folders it would create, and how many files are already the same size.
    /// </summary>
    public static string SummariseMirror(IEnumerable<MirrorItem> plan)
    {
        var items = plan.ToList();
        var downloads = items.Where(i => i.Action == "download").ToList();
        var folders = items.Count(i => i.Action == "create-dir");
        var skips = items.Count(i => i.Action == "skip");

        var parts = new List<string>();
        if (downloads.Count > 0)
            parts.Add($"{downloads.Count} file(s), {SftpEntry.FormatSize(downloads.Sum(i => i.Size))}");
        if (folders > 0) parts.Add(folders == 1 ? "1 folder" : $"{folders} folders");
        if (skips > 0) parts.Add(skips == 1 ? "1 up to date" : $"{skips} up to date");
        return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts);
    }

    /// <summary>
    /// Downloads a remote tree into a local one. Size decides what is unchanged; symlinks are
    /// skipped so a mirror can never walk out of the tree it was pointed at. A dry run returns the
    /// same plan without touching the disk.
    /// </summary>
    public async Task<List<MirrorItem>> MirrorAsync(string remoteRoot, string localRoot, bool dryRun,
                                                    Action<string>? log, CancellationToken token)
    {
        var plan = new List<MirrorItem>();
        var pending = new Queue<(string Remote, string Local)>();
        pending.Enqueue((remoteRoot.TrimEnd('/'), localRoot));

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (remoteDir, localDir) = pending.Dequeue();
            if (!dryRun) Directory.CreateDirectory(localDir);

            foreach (var entry in await ListDirectoryAsync(remoteDir))
            {
                if (entry.Name is "." or ".." || entry.IsLink) continue;
                var local = Path.Combine(localDir, entry.Name);

                if (entry.IsDirectory)
                {
                    plan.Add(PlanMirror(entry, local, Directory.Exists(local), 0));
                    log?.Invoke($"dir      {entry.FullPath} -> {local}");
                    pending.Enqueue((entry.FullPath, local));
                    continue;
                }

                var exists = File.Exists(local);
                var size = exists ? new FileInfo(local).Length : 0;
                var item = PlanMirror(entry, local, exists, size);
                plan.Add(item);
                log?.Invoke($"{item.Action,-8} {entry.FullPath} -> {local}");
                if (item.Action == "download" && !dryRun)
                    await DownloadAsync(entry.FullPath, local, null, token);
            }
        }

        return plan;
    }

    private const byte FXP_INIT = 1, FXP_VERSION = 2, FXP_OPEN = 3, FXP_CLOSE = 4,
        FXP_READ = 5, FXP_WRITE = 6, FXP_LSTAT = 7, FXP_SETSTAT = 9,
        FXP_OPENDIR = 11, FXP_READDIR = 12, FXP_REMOVE = 13, FXP_MKDIR = 14,
        FXP_RMDIR = 15, FXP_REALPATH = 16, FXP_STAT = 17, FXP_RENAME = 18,
        FXP_STATUS = 101, FXP_HANDLE = 102, FXP_DATA = 103, FXP_NAME = 104, FXP_ATTRS = 105;

    private const uint FXF_READ = 0x01, FXF_WRITE = 0x02, FXF_CREAT = 0x08, FXF_TRUNC = 0x10;
    private const uint ATTR_SIZE = 0x01, ATTR_PERMISSIONS = 0x04, ATTR_ACMODTIME = 0x08;

    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Packet>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId = 1;
    private TaskCompletionSource<Packet>? _versionTcs;
    private Thread? _reader;
    private Thread? _errorThread;
    private volatile bool _closed;

    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }
    public string HomePath { get; private set; } = ".";

    public async Task ConnectAsync(Session session, string? secret)
    {
        var args = SshLauncher.BuildSubsystemArguments(session, "sftp");
        var info = new ProcessStartInfo(SshLauncher.SshExe, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(secret))
        {
            info.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? "";
            info.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            info.Environment["CONDUIT_ASKPASS"] = CredentialVault.StoreAskPass(session.Id, secret!);
        }

        _process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ssh.exe");
        _input = _process.StandardInput.BaseStream;
        _output = _process.StandardOutput.BaseStream;

        // Keep stderr: it carries the only useful explanation when the subsystem dies.
        _errorThread = new Thread(() =>
        {
            try
            {
                var text = _process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(text)) LastError = text.Trim();
            }
            catch { }
        })
        { IsBackground = true, Name = "sftp-stderr" };
        _errorThread.Start();

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "sftp-reader" };
        _reader.Start();

        uint version;
        try
        {
            version = await InitAsync();
        }
        catch (Exception ex)
        {
            throw new IOException(string.IsNullOrWhiteSpace(LastError) ? ex.Message : LastError);
        }
        if (version < 3) throw new InvalidOperationException("The server speaks an unsupported SFTP version.");

        HomePath = await RealPathAsync(".");
        IsConnected = true;
    }

    private Task<uint> InitAsync()
    {
        // FXP_VERSION always carries id 0, so it cannot go through the id table.
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        _versionTcs = tcs;

        var payload = new MemoryStream();
        WriteUInt32(payload, 5);
        payload.WriteByte(FXP_INIT);
        WriteUInt32(payload, 3);
        Send(payload.ToArray());

        return AwaitVersion(tcs.Task);
    }

    private async Task<uint> AwaitVersion(Task<Packet> task)
    {
        var packet = await task;
        var reader = new PacketReader(packet.Payload);
        reader.ReadByte(); // type
        return reader.ReadUInt32();
    }

    // ---------------------------------------------------------------- operations

    public async Task<string> RealPathAsync(string path)
    {
        var packet = await RequestAsync(FXP_REALPATH, w => w.WriteString(path));
        var reader = new PacketReader(packet.Payload);
        reader.ReadHeader();
        var count = reader.ReadUInt32();
        if (count == 0) return path;
        var name = reader.ReadString();
        reader.ReadString();
        ReadAttributes(reader);
        return name;
    }

    public async Task<List<SftpEntry>> ListDirectoryAsync(string path)
    {
        var handlePacket = await RequestAsync(FXP_OPENDIR, w => w.WriteString(path));
        var handleReader = new PacketReader(handlePacket.Payload);
        handleReader.ReadHeader();
        var handle = handleReader.ReadBytes();

        var entries = new List<SftpEntry>();
        var basePath = path.TrimEnd('/');

        while (true)
        {
            var packet = await RequestRawAsync(FXP_READDIR, w => w.WriteBytes(handle));
            var reader = new PacketReader(packet.Payload);
            var type = reader.ReadHeader();
            if (type == FXP_STATUS)
            {
                var code = reader.ReadUInt32();
                if (code == 1) break; // EOF
                throw new SftpException(StatusText(code));
            }

            var count = reader.ReadUInt32();
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadString();
                var longName = reader.ReadString();
                var attrs = ReadAttributes(reader);
                if (name is "." or "..") continue;

                var isLink = (attrs.Permissions ?? 0) >> 12 == 0xA;
                entries.Add(new SftpEntry
                {
                    Name = name,
                    FullPath = basePath + "/" + name,
                    IsDirectory = ((attrs.Permissions ?? 0) & 0xF000) == 0x4000,
                    IsLink = isLink,
                    Size = attrs.Size,
                    Permissions = attrs.Permissions,
                    Modified = attrs.Modified
                });
            }
        }

        await RequestAsync(FXP_CLOSE, w => w.WriteBytes(handle));
        return entries;
    }

    public async Task DownloadAsync(string remotePath, string localPath, Action<long, long>? progress,
                                    CancellationToken token, bool resume = false)
    {
        var open = await RequestAsync(FXP_OPEN, w =>
        {
            w.WriteString(remotePath);
            WriteUInt32(w, FXF_READ);
            WriteUInt32(w, 0);
        });
        var handle = ReadHandle(open);

        var total = await StatAsync(remotePath).ContinueWith(t => t.Result?.Size ?? 0L);
        var start = resume && File.Exists(localPath)
            ? ResumeOffset(new FileInfo(localPath).Length, total)
            : 0;

        // Pipelining: keep a window of READs outstanding. One request at a time costs one
        // round trip per 32 KB, which is what makes SFTP crawl on a high-latency link; the
        // replies may come back out of order, so they are consumed in submission order and
        // written sequentially.
        const int chunkSize = 32768;
        const int window = 32;
        long nextOffset = start;
        long written = start;
        var inFlight = new Queue<(long Offset, Task<Packet> Task)>();
        var eof = false;

        try
        {
            using var file = start > 0
                ? new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.None, 65536, true)
                : new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            if (start > 0) file.Seek(start, SeekOrigin.Begin);
            while (true)
            {
                token.ThrowIfCancellationRequested();

                while (!eof && inFlight.Count < window)
                {
                    var at = nextOffset;
                    nextOffset += chunkSize;
                    inFlight.Enqueue((at, RequestRawAsync(FXP_READ, w =>
                    {
                        w.WriteBytes(handle);
                        WriteUInt64(w, (ulong)at);
                        WriteUInt32(w, chunkSize);
                    })));
                }

                if (inFlight.Count == 0) break;

                var request = await inFlight.Dequeue().Task;
                if (eof) continue;                      // requests issued past EOF: discard

                var reader = new PacketReader(request.Payload);
                var type = reader.ReadHeader();
                if (type == FXP_STATUS)
                {
                    var code = reader.ReadUInt32();
                    if (code == 1) { eof = true; continue; }
                    throw new SftpException(StatusText(code));
                }

                var data = reader.ReadBytes();
                if (data.Length == 0) { eof = true; continue; }
                await file.WriteAsync(data, token);
                written += data.Length;
                progress?.Invoke(written, total);

                // A short read means the end of the file; anything already in flight will
                // come back as STATUS EOF and is drained above.
                if (data.Length < chunkSize) eof = true;
            }
        }
        finally
        {
            await RequestAsync(FXP_CLOSE, w => w.WriteBytes(handle)).ConfigureAwait(false);
        }
    }

    public async Task UploadAsync(string localPath, string remotePath, Action<long, long>? progress,
                                  CancellationToken token, bool resume = false)
    {
        var total = new FileInfo(localPath).Length;
        var remoteSize = resume ? (await StatAsync(remotePath))?.Size ?? 0 : 0;
        var start = resume ? ResumeOffset(remoteSize, total) : 0;

        var open = await RequestAsync(FXP_OPEN, w =>
        {
            w.WriteString(remotePath);
            WriteUInt32(w, start > 0 ? FXF_WRITE | FXF_CREAT : FXF_WRITE | FXF_CREAT | FXF_TRUNC);
            WriteUInt32(w, ATTR_PERMISSIONS);
            WriteUInt32(w, 0x1A4); // 0644
        });
        var handle = ReadHandle(open);

        long submitted = start;
        long acknowledged = 0;
        const int window = 32;
        var buffer = new byte[32768];
        var pending = new Queue<(Task<Packet> Task, int Length)>();

        try
        {
            using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (start > 0) file.Seek(start, SeekOrigin.Begin);
            while (true)
            {
                token.ThrowIfCancellationRequested();

                // Same window as downloads: keep up to 32 WRITEs in flight, but only report
                // progress when the server has acknowledged a write, so a bar cannot finish
                // before the data has landed.
                while (pending.Count < window)
                {
                    var read = await file.ReadAsync(buffer, token);
                    if (read <= 0) break;

                    var chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);
                    var at = submitted;
                    submitted += read;

                    pending.Enqueue((RequestAsync(FXP_WRITE, w =>
                    {
                        w.WriteBytes(handle);
                        WriteUInt64(w, (ulong)at);
                        w.WriteBytes(chunk);
                    }), read));
                }

                if (pending.Count == 0) break;

                var (task, length) = pending.Dequeue();
                await task;
                acknowledged += length;
                progress?.Invoke(acknowledged, total);
            }
        }
        finally
        {
            await RequestAsync(FXP_CLOSE, w => w.WriteBytes(handle)).ConfigureAwait(false);
        }
    }

    public async Task<SftpEntry?> StatAsync(string path, bool followLinks = true)
    {
        Packet packet;
        try
        {
            packet = await RequestAsync(followLinks ? FXP_STAT : FXP_LSTAT, w => w.WriteString(path));
        }
        catch (SftpException ex) when (ex.Status == 2)
        {
            return null;   // SSH_FX_NO_SUCH_FILE: absent, not "could not check"
        }
        var reader = new PacketReader(packet.Payload);
        var type = reader.ReadHeader();
        if (type == FXP_STATUS) return null;
        var attrs = ReadAttributes(reader);
        return new SftpEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = ((attrs.Permissions ?? 0) & 0xF000) == 0x4000,
            Size = attrs.Size,
            Permissions = attrs.Permissions,
            Modified = attrs.Modified
        };
    }

    public Task MkdirAsync(string path) => RequestAsync(FXP_MKDIR, w =>
    {
        w.WriteString(path);
        WriteUInt32(w, ATTR_PERMISSIONS);
        WriteUInt32(w, 0x1ED); // 0755
    });

    public Task RemoveAsync(string path) => RequestAsync(FXP_REMOVE, w => w.WriteString(path));

    public Task RmdirAsync(string path) => RequestAsync(FXP_RMDIR, w => w.WriteString(path));

    public Task RenameAsync(string from, string to) => RequestAsync(FXP_RENAME, w =>
    {
        w.WriteString(from);
        w.WriteString(to);
    });

    public Task ChmodAsync(string path, uint mode) => RequestAsync(FXP_SETSTAT, w =>
    {
        w.WriteString(path);
        WriteUInt32(w, ATTR_PERMISSIONS);
        WriteUInt32(w, mode);
    });

    // ---------------------------------------------------------------- plumbing

    private sealed class Packet
    {
        public required byte[] Payload { get; init; }
    }

    private sealed class SftpException : Exception
    {
        public SftpException(string message, uint status = 0) : base(message) { Status = status; }

        /// <summary>The SSH_FX_* code the server answered with (2 = no such file).</summary>
        public uint Status { get; }
    }

    private static string StatusText(uint code) => code switch
    {
        2 => "No such file or directory.",
        3 => "Permission denied.",
        4 => "The server rejected the operation.",
        8 => "Operation not supported by this server.",
        _ => "SFTP error " + code + "."
    };

    private uint NextId() => (uint)Interlocked.Increment(ref _nextId);

    private TaskCompletionSource<Packet> NewRequest(uint id)
    {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        return tcs;
    }

    private async Task<Packet> RequestAsync(byte type, Action<MemoryStream> writePayload)
    {
        if (_closed || _input is null) throw new IOException("Not connected.");
        var id = NextId();

        var payload = new MemoryStream();
        var body = new MemoryStream();
        body.WriteByte(type);
        WriteUInt32(body, id);
        writePayload(body);

        var bodyBytes = body.ToArray();
        WriteUInt32(payload, (uint)bodyBytes.Length);
        payload.Write(bodyBytes);

        var tcs = NewRequest(id);
        Send(payload.ToArray());

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException("The SFTP server did not answer in time.");
        }

        var packet = await tcs.Task;
        var reader = new PacketReader(packet.Payload);
        var responseType = reader.ReadByte();
        reader.ReadUInt32(); // id

        if (responseType == FXP_STATUS)
        {
            var code = reader.ReadUInt32();
            if (code != 0) throw new SftpException(StatusText(code), code);
        }

        return packet;
    }

    /// <summary>Like RequestAsync but returns STATUS packets instead of throwing (EOF is normal here).</summary>
    private async Task<Packet> RequestRawAsync(byte type, Action<MemoryStream> writePayload)
    {
        if (_closed || _input is null) throw new IOException("Not connected.");
        var id = NextId();

        var payload = new MemoryStream();
        var body = new MemoryStream();
        body.WriteByte(type);
        WriteUInt32(body, id);
        writePayload(body);

        var bodyBytes = body.ToArray();
        WriteUInt32(payload, (uint)bodyBytes.Length);
        payload.Write(bodyBytes);

        var tcs = NewRequest(id);
        Send(payload.ToArray());

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException("The SFTP server did not answer in time.");
        }
        return await tcs.Task;
    }

    private async void Send(byte[] packet)
    {
        if (_input is null) return;
        await _writeLock.WaitAsync();
        try
        {
            await _input.WriteAsync(packet, 0, packet.Length);
            await _input.FlushAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            FailAll(ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void ReadLoop()
    {
        var header = new byte[4];
        try
        {
            while (!_closed && _output is not null)
            {
                if (!ReadExactly(header, 4)) break;
                var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length <= 0 || length > 32 * 1024 * 1024) break;

                var payload = new byte[length];
                if (!ReadExactly(payload, length)) break;

                var reader = new PacketReader(payload);
                var type = reader.ReadByte();
                if (type == FXP_VERSION)
                {
                    _versionTcs?.TrySetResult(new Packet { Payload = payload });
                    continue;
                }

                var id = length >= 5 ? reader.ReadUInt32() : 0;
                if (id != 0 && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(new Packet { Payload = payload });
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        finally { FailAll(new IOException(LastError ?? "The SFTP channel closed.")); }
    }

    private bool ReadExactly(byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = _output!.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private void FailAll(Exception ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);

        // FXP_VERSION always carries id 0 and so never enters the id table: a connection that dies during
        // the handshake used to leave InitAsync waiting for ever, with no error and no timeout.
        var version = _versionTcs;
        _versionTcs = null;
        version?.TrySetException(ex);
    }

    private static byte[] ReadHandle(Packet packet)
    {
        var reader = new PacketReader(packet.Payload);
        reader.ReadHeader();
        return reader.ReadBytes();
    }

    private sealed record Attributes(long Size, uint? Permissions, DateTime? Modified);

    private static Attributes ReadAttributes(PacketReader reader)
    {
        var flags = reader.ReadUInt32();
        long size = 0;
        uint? permissions = null;
        DateTime? modified = null;

        if ((flags & ATTR_SIZE) != 0) size = (long)reader.ReadUInt64();
        if ((flags & 0x02) != 0) { reader.ReadUInt32(); reader.ReadUInt32(); }
        if ((flags & ATTR_PERMISSIONS) != 0) permissions = reader.ReadUInt32();
        if ((flags & ATTR_ACMODTIME) != 0)
        {
            var atime = reader.ReadUInt32();
            var mtime = reader.ReadUInt32();
            _ = atime;
            modified = DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime;
        }
        if ((flags & 0x80000000) != 0)
        {
            var count = reader.ReadUInt32();
            for (var i = 0; i < count; i++) { reader.ReadString(); reader.ReadString(); }
        }

        return new Attributes(size, permissions, modified);
    }

    private sealed class PacketReader
    {
        private readonly byte[] _data;
        private int _offset;

        public PacketReader(byte[] data) => _data = data;

        public byte ReadByte() => _data[_offset++];

        /// <summary>Reads the packet type and skips the 4-byte request id.</summary>
        public byte ReadHeader()
        {
            var type = ReadByte();
            _offset += 4;
            return type;
        }

        public uint ReadUInt32()
        {
            var value = (uint)((_data[_offset] << 24) | (_data[_offset + 1] << 16) |
                               (_data[_offset + 2] << 8) | _data[_offset + 3]);
            _offset += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++) value = (value << 8) | _data[_offset + i];
            _offset += 8;
            return value;
        }

        public string ReadString()
        {
            var length = (int)ReadUInt32();
            var text = Encoding.UTF8.GetString(_data, _offset, length);
            _offset += length;
            return text;
        }

        public byte[] ReadBytes()
        {
            var length = (int)ReadUInt32();
            var bytes = new byte[length];
            Array.Copy(_data, _offset, bytes, 0, length);
            _offset += length;
            return bytes;
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        for (var i = 7; i >= 0; i--) stream.WriteByte((byte)(value >> (i * 8)));
    }

    private sealed class PacketWriter
    {
        private readonly MemoryStream _stream = new();

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt32(_stream, (uint)bytes.Length);
            _stream.Write(bytes);
        }

        public void WriteBytes(byte[] value)
        {
            WriteUInt32(_stream, (uint)value.Length);
            _stream.Write(value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1500);
            }
        }
        catch { }
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        _process?.Dispose();
    }
}

internal static class SftpStreamExtensions
{
    public static void WriteString(this Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static void WriteBytes(this Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value, 0, value.Length);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
