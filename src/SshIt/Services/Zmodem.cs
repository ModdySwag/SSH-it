using System.IO;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// ZMODEM (sz/rz) carried inside the terminal byte stream.
///
/// The protocol itself is Chuck Forsberg's public domain design; the framing rules
/// implemented here follow the reference document (ZPAD/ZDLE headers, hex and binary
/// headers, ZCRCE/ZCRCG/ZCRCQ/ZCRCW data subpackets, CRC-16 and CRC-32).
///
/// Why this lives in the terminal at all: telnet, serial and BBS-style hosts have no
/// SFTP, and "sz file" is the only way a file comes back. The session takes over the
/// output stream when a ZRQINIT header is seen, and gives it back when the transfer
/// ends, so the VT parser never sees binary frames.
///
/// Two deliberate refusals:
///  - a ZCOMMAND frame is never executed (the spec's Trojan Horse hazard);
///  - a received file name is reduced to a single leaf name inside the download
///    folder, so "..\..\windows\system32\x" cannot escape.
///
/// The session runs its own thread: the terminal hands bytes in with Feed(), the
/// protocol and the writes happen off the UI thread, and progress comes back through
/// events the caller marshals.
/// </summary>
public sealed class ZmodemSession
{
    // ---------------------------------------------------------------- constants

    private const byte ZPAD = 0x2A;    // '*'
    private const byte ZDLE = 0x18;    // CAN
    private const byte ZBIN = 0x41;    // 'A' 16 bit CRC binary header
    private const byte ZHEX = 0x42;    // 'B' hex header
    private const byte ZBIN32 = 0x43;  // 'C' 32 bit CRC binary header

    private const byte ZCRCE = 0x68;   // 'h' end of frame
    private const byte ZCRCG = 0x69;   // 'i' more data follows
    private const byte ZCRCQ = 0x6A;   // 'j' more data follows, ack wanted
    private const byte ZCRCW = 0x6B;   // 'k' more data follows, ack wanted, sender waits

    private const int ZRQINIT = 0;
    private const int ZRINIT = 1;
    private const int ZSINIT = 2;
    private const int ZACK = 3;
    private const int ZFILE = 4;
    private const int ZSKIP = 5;
    private const int ZNAK = 6;
    private const int ZABORT = 7;
    private const int ZFIN = 8;
    private const int ZRPOS = 9;
    private const int ZDATA = 10;
    private const int ZEOF = 11;
    private const int ZFERR = 12;
    private const int ZCRC = 13;
    private const int ZCHALLENGE = 14;
    private const int ZCOMPL = 15;
    private const int ZCAN = 16;
    private const int ZFREECNT = 17;
    private const int ZCOMMAND = 18;

    private const byte CANFDX = 0x01;
    private const byte CANOVIO = 0x02;
    private const byte CANFC32 = 0x40;

    /// <summary>ZMODEM starts a data frame with one subpacket of this size or less.</summary>
    // 1024 is the classic ZMODEM subpacket and what lrzsz's rz is sized for; an 8192-byte
    // frame (before escaping) made rz answer Bad CRC / Garbage count exceeded on the first
    // data frame while the small ZFILE metadata frame went through.
    private const int SubpacketSize = 1024;

    private enum Mode { Hunt, Data, Finished }

    // ---------------------------------------------------------------- state

    private readonly ZmodemRole _role;
    private readonly Action<byte[]> _write;
    private readonly string _downloadDir;
    private readonly string? _sendPath;

    private readonly object _gate = new();
    private readonly List<byte[]> _incoming = new();
    private readonly List<byte> _buffer = new();
    private readonly List<byte> _sub = new();
    private int _pos;
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;

    private Mode _mode = Mode.Hunt;
    private bool _frameCrc32;
    private int _expecting = -1;
    private bool _discard;

    // receive side
    private FileStream? _file;
    private long _filePos;
    private long _fileLength;
    private string _fileName = "";
    private string? _destPath;
    private int _filesReceived;

    // send side
    private byte[] _txData = Array.Empty<byte>();
    private long _txSent;
    private long _txLength;
    private string _txName = "";
    private bool _txCrc32;
    private bool _txStarted;
    private bool _txAwaitEofAck;
    // True between the ZDATA header and the frame that ends the file. A ZACK can arrive for
    // the ZFILE metadata frame (ZCRCW asks for one) before any ZDATA header exists; sending
    // subpackets at that point puts bare data on the wire with nothing to frame it.
    private bool _txStreaming;

    // timing
    private DateTime _lastSend = DateTime.UtcNow;
    private DateTime _lastProgress = DateTime.UtcNow;
    private readonly DateTime _started = DateTime.UtcNow;

    public ZmodemRole Role => _role;
    public bool Finished { get; private set; }
    public bool Failed { get; private set; }
    public string? Message { get; private set; }
    public string? SavedPath { get; private set; }
    public int FilesReceived => _filesReceived;
    public string CurrentName => _role == ZmodemRole.Receive ? _fileName : _txName;
    public long Bytes => _role == ZmodemRole.Receive ? _filePos : _txSent;
    public long Total => _role == ZmodemRole.Receive ? _fileLength : _txLength;

    /// <summary>Progress and phase changes. Raised on the session thread.</summary>
    public event Action<ZmodemSession>? Changed;

    /// <summary>One line for a human: what happened, in words.</summary>
    public event Action<string>? Notice;

    /// <summary>ok, one-line summary. Raised once, on the session thread.</summary>
    public event Action<bool, string>? Completed;

    // ---------------------------------------------------------------- lifecycle

    public ZmodemSession(ZmodemRole role, Action<byte[]> write, string downloadDir, string? sendPath)
    {
        _role = role;
        _write = write;
        _downloadDir = downloadDir;
        _sendPath = sendPath;

        if (role == ZmodemRole.Send)
        {
            if (sendPath is null || !File.Exists(sendPath))
                throw new FileNotFoundException("Nothing to send: " + sendPath, sendPath);
            _txData = File.ReadAllBytes(sendPath);
            _txLength = _txData.Length;
            _txName = Path.GetFileName(sendPath);
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "zmodem-" + role };
        _thread.Start();

        // A receiver announces itself at once; that is what a sender is waiting for when
        // the user typed "sz file" and the first ZRQINIT reached us.
        if (role == ZmodemRole.Receive) SendRinit();
    }

    /// <summary>Terminal output for this session. Ends the VT parser's part of the stream.</summary>
    public void Feed(byte[] data, int count) => Feed(data, 0, count);

    public void Feed(byte[] data, int offset, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(data, offset, copy, 0, count);
        lock (_gate) _incoming.Add(copy);
        _wake.Set();
    }

    public void Abort(string why = "cancelled")
    {
        try
        {
            var cancel = new byte[18];
            for (var i = 0; i < 8; i++) cancel[i] = ZDLE;
            for (var i = 8; i < 18; i++) cancel[i] = 0x08;
            _write(cancel);
        }
        catch { }
        Finish(false, "Transfer " + why + ".");
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        var thread = _thread;
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(1500);
        try { _file?.Dispose(); } catch { }
        _file = null;
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            while (!_stop && !Finished)
            {
                _wake.Wait(200);
                _wake.Reset();
                DrainIncoming();
                if (Finished) break;
                // A sender is a stream, not a request/response. Keep filling the pipe in 1 MB
                // slices and let the loop parse headers between them: waiting for a ZACK that
                // ZCRCG never asks for stalled every upload at exactly one slice.
                if (_role == ZmodemRole.Send && _txStreaming && !_txAwaitEofAck && _txSent < _txLength)
                    SendDataFrames();
                CheckTimeouts();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "zmodem");
            Finish(false, "ZMODEM failed: " + ex.Message);
        }
        finally
        {
            try { _file?.Flush(); _file?.Dispose(); } catch { }
            _file = null;
        }
    }

    private void DrainIncoming()
    {
        List<byte[]> chunks;
        lock (_gate)
        {
            if (_incoming.Count == 0) return;
            chunks = new List<byte[]>(_incoming);
            _incoming.Clear();
        }
        foreach (var chunk in chunks) _buffer.AddRange(chunk);
        Parse();
    }

    // ---------------------------------------------------------------- parsing

    private void Parse()
    {
        while (!Finished)
        {
            if (_mode == Mode.Data)
            {
                if (!ParseData()) return;
            }
            else if (_mode == Mode.Hunt)
            {
                if (!ParseHeader()) return;
            }
            else return;
        }
    }

    /// <summary>Looks for a header. False means "need more bytes"; a non-header ZDLE pair is skipped.</summary>
    private bool ParseHeader()
    {
        while (true)
        {
            var start = -1;
            for (var i = _pos; i < _buffer.Count; i++)
            {
                if (_buffer[i] == ZDLE) { start = i; break; }
            }
            if (start < 0) { _pos = _buffer.Count; Trim(); return false; }
            if (start + 1 >= _buffer.Count) { _pos = start; Trim(); return false; }

            var kind = _buffer[start + 1];
            if (kind == ZHEX || kind == ZBIN || kind == ZBIN32)
            {
                _pos = start;
                return kind == ZHEX ? ReadHexHeader() : ReadBinaryHeader(kind == ZBIN32);
            }

            // A stray ZDLE outside data: drop it. Five CANs in a row is an abort.
            var cans = 0;
            for (var i = start; i < _buffer.Count && _buffer[i] == ZDLE; i++) cans++;
            if (cans >= 5)
            {
                Finish(false, "The far end cancelled the transfer.");
                return false;
            }
            _pos = start + 1;
        }
    }

    private bool ReadHexHeader()
    {
        // * * ZDLE B TYPE F3 F2 F1 F0 CRC1 CRC2 CR LF XON   (each field two lowercase hex digits)
        var p = _pos + 2;                       // skip ZDLE, ZHEX
        if (_buffer.Count < p + 14) { Trim(); return false; }

        var hex = new int[14];
        for (var i = 0; i < 14; i++)
        {
            var v = HexValue(_buffer[p + i]);
            if (v < 0) { _pos = p + i + 1; return true; }   // not a header after all
            hex[i] = v;
        }

        var type = (hex[0] << 4) | hex[1];
        var data = new uint[4];
        for (var i = 0; i < 4; i++) data[i] = (uint)((hex[2 + i * 2] << 4) | hex[3 + i * 2]);
        var crcRx = (ushort)((((hex[10] << 4) | hex[11]) << 8) | (hex[12] << 4) | hex[13]);

        var crcBytes = new byte[5];
        crcBytes[0] = (byte)type;
        for (var i = 0; i < 4; i++) crcBytes[1 + i] = (byte)data[i];
        var crc = Crc16(crcBytes, 0, 5);
        if (crc != crcRx) { _pos = p + 14; return true; }   // garbled header, keep hunting

        _pos = p + 14;
        // Trailing CR and optional LF/XON are not required by every sender; skip what is there.
        // lrzsz writes the LF with its high bit set (0x8A), so both forms are accepted.
        while (_pos < _buffer.Count &&
               (_buffer[_pos] == 0x0D || _buffer[_pos] == 0x0A || _buffer[_pos] == 0x8A || _buffer[_pos] == 0x11))
            _pos++;

        var value = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
        HandleHeader(type, value, crc32: false, flags: data);
        return true;
    }

    /// <summary>
    /// One ZDLE-escaped byte at index. False means the escape is not complete yet.
    /// 0x18 is doubled, 0x40..0x5F invert bit 6 back to the control character, and the
    /// two 8th-bit forms (0x3F/0xBF) are the escapes for 0x7F and 0xFF.
    /// </summary>
    private bool TryReadEscaped(int index, out byte value, out int next)
    {
        value = 0;
        next = index;
        if (index >= _buffer.Count) return false;

        var b = _buffer[index];
        if (b != ZDLE) { value = b; next = index + 1; return true; }
        if (index + 1 >= _buffer.Count) return false;

        var n = _buffer[index + 1];
        next = index + 2;
        if (n == ZDLE) { value = ZDLE; return true; }
        if ((n & 0x60) == 0x40) { value = (byte)(n ^ 0x40); return true; }
        if (n == 0x3F) { value = 0x7F; return true; }
        if (n == 0xBF) { value = 0xFF; return true; }

        // Not an escape this implementation knows: keep the ZDLE as data and move on.
        value = ZDLE;
        next = index + 1;
        return true;
    }

    private bool ReadBinaryHeader(bool crc32)
    {
        // * ZDLE A|C TYPE F3 F2 F1 F0 CRC...  every byte after the introducer is ZDLE escaped.
        var p = _pos + 2;
        var raw = new byte[crc32 ? 9 : 7];
        for (var i = 0; i < raw.Length; i++)
        {
            if (p >= _buffer.Count) { Trim(); return false; }
            if (!TryReadEscaped(p, out var b, out p)) { Trim(); return false; }
            raw[i] = b;
        }

        var type = raw[0];
        var data = new uint[4];
        for (var i = 0; i < 4; i++) data[i] = raw[1 + i];

        if (crc32)
        {
            var crcRx = (uint)(raw[5] | (raw[6] << 8) | (raw[7] << 16) | (raw[8] << 24));
            if (Crc32(raw, 0, 5) != crcRx) { _pos = p; return true; }
        }
        else
        {
            var crcRx = (ushort)((raw[5] << 8) | raw[6]);
            if (Crc16(raw, 0, 5) != crcRx) { _pos = p; return true; }
        }

        _pos = p;
        var value = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
        HandleHeader(type, value, crc32, data);
        return true;
    }

    private void HandleHeader(int type, uint value, bool crc32, uint[] flags)
    {
        Log.Info($"zmodem: header {Name(type)} value={value} crc32={crc32} role={_role}");
        _lastProgress = DateTime.UtcNow;

        switch (_role)
        {
            case ZmodemRole.Receive: ReceiveHeader(type, value, crc32); break;
            case ZmodemRole.Send: SendHeader(type, value, crc32, flags); break;
        }
    }

    private void ReceiveHeader(int type, uint value, bool crc32)
    {
        switch (type)
        {
            case ZRQINIT:
                SendRinit();
                break;

            case ZFILE:
                _frameCrc32 = crc32;
                _expecting = ZFILE;
                _sub.Clear();
                _mode = Mode.Data;
                break;

            case ZSINIT:
                _frameCrc32 = crc32;
                _expecting = ZSINIT;
                _sub.Clear();
                _mode = Mode.Data;
                break;

            case ZDATA:
                if (value != (uint)_filePos || _file is null)
                {
                    // The sender and we disagree about the offset: ask for ours.
                    _discard = true;
                    SendHeader(ZRPOS, (uint)_filePos);
                }
                else _discard = false;
                _frameCrc32 = crc32;
                _expecting = ZDATA;
                _sub.Clear();
                _mode = Mode.Data;
                break;

            case ZEOF:
                EndFile((long)value);
                break;

            case ZFIN:
                SendHeader(ZFIN);
                Finish(true, Summarise());
                break;

            case ZCOMMAND:
                // Never. A ZMODEM peer must not be able to run anything here.
                Log.Warn("zmodem: refused a ZCOMMAND frame");
                SendHeader(ZFIN);
                Finish(false, "Refused a remote command (ZCOMMAND) — SSH-it never runs commands sent over ZMODEM.");
                break;

            case ZRINIT:
            case ZACK:
            case ZSKIP:
            case ZNAK:
            case ZABORT:
            case ZFERR:
            case ZCAN:
                break;   // echoes and refusals: nothing useful for a receiver to do
        }
    }

    private void SendHeader(int type, uint value, bool crc32, uint[] flags)
    {
        switch (type)
        {
            case ZRQINIT:
                // rz is up and waiting. Offer the file.
                if (!_txStarted) SendZfile();
                break;

            case ZRINIT:
                _txCrc32 = (flags[3] & CANFC32) != 0;
                if (!_txStarted) SendZfile();
                else if (_txAwaitEofAck) StartFinish();
                else SendDataFrames();
                break;

            case ZRPOS:
                _txSent = value;
                _txStarted = true;
                _txAwaitEofAck = false;
                SendDataHeader(value);
                break;

            case ZACK:
                Pump();
                break;

            case ZSKIP:
                Finish(false, "The far end skipped the file.");
                break;

            case ZABORT:
                Finish(false, "The far end aborted the transfer.");
                break;

            case ZFIN:
                SendText("OO");
                Finish(true, "Sent " + _txName + " (" + _txLength + " bytes).");
                break;

            case ZCOMMAND:
            case ZCHALLENGE:
                SendHeader(ZFIN);
                Finish(false, "The far end demanded a command or challenge handshake.");
                break;
        }
    }

    // ---------------------------------------------------------------- data mode

    /// <summary>Consumes one data subpacket. False means "need more bytes".</summary>
    private bool ParseData()
    {
        while (true)
        {
            if (_pos >= _buffer.Count) { Trim(); return false; }

            var b = _buffer[_pos];
            if (b != ZDLE)
            {
                _sub.Add(b);
                _pos++;
                continue;
            }

            if (_pos + 1 >= _buffer.Count) { Trim(); return false; }
            var n = _buffer[_pos + 1];

            if (n == ZCRCE || n == ZCRCG || n == ZCRCQ || n == ZCRCW)
            {
                // Frame end. CRC over the data and the frame-end byte, then the CRC bytes, escaped.
                var crcLen = _frameCrc32 ? 4 : 2;
                var p = _pos + 2;
                var crc = new byte[crcLen];
                for (var i = 0; i < crcLen; i++)
                {
                    if (p >= _buffer.Count) { Trim(); return false; }
                    if (!TryReadEscaped(p, out var c, out p)) { Trim(); return false; }
                    crc[i] = c;
                }

                var ok = _frameCrc32
                    ? Crc32WithEnd(_sub.ToArray(), n) == BitConverter.ToUInt32(crc, 0)
                    : Crc16WithEnd(_sub.ToArray(), n) == (ushort)((crc[0] << 8) | crc[1]);

                _pos = p;
                if (!ok)
                {
                    Log.Warn("zmodem: subpacket CRC mismatch");
                    _sub.Clear();
                    if (_expecting == ZDATA && _file is not null) SendHeader(ZRPOS, (uint)_filePos);
                    else SendHeader(ZNAK);
                    _mode = Mode.Hunt;
                    return true;
                }

                OnSubpacket(n);
                return true;
            }

            // An escaped data byte.
            if (n == ZDLE) { _sub.Add(ZDLE); _pos += 2; continue; }
            if ((n & 0x60) == 0x40) { _sub.Add((byte)(n ^ 0x40)); _pos += 2; continue; }
            if (n == 0x3F) { _sub.Add(0x7F); _pos += 2; continue; }
            if (n == 0xBF) { _sub.Add(0xFF); _pos += 2; continue; }

            _pos += 2;   // unknown escape: drop it
        }
    }

    private void OnSubpacket(byte frameEnd)
    {
        var data = _sub.ToArray();
        _sub.Clear();

        switch (_expecting)
        {
            case ZFILE:
                AcceptFileInfo(data);
                _expecting = -1;
                _mode = Mode.Hunt;
                return;

            case ZSINIT:
                _expecting = -1;
                _mode = Mode.Hunt;
                SendHeader(ZACK);
                return;

            case ZDATA:
                if (!_discard && _file is not null)
                {
                    _file.Write(data, 0, data.Length);
                    _filePos += data.Length;
                    _lastProgress = DateTime.UtcNow;
                    Changed?.Invoke(this);
                }

                if (frameEnd == ZCRCE)
                {
                    _expecting = -1;
                    _mode = Mode.Hunt;      // ZEOF (or another ZDATA) follows
                }
                else if (frameEnd == ZCRCG || frameEnd == ZCRCQ)
                {
                    // ZCRCG continues silently; ZCRCQ wants an ack.
                    if (frameEnd == ZCRCQ) SendHeader(ZACK, (uint)_filePos);
                }
                else if (frameEnd == ZCRCW)
                {
                    SendHeader(ZACK, (uint)_filePos);
                }
                return;
        }

        _mode = Mode.Hunt;
    }

    private void AcceptFileInfo(byte[] info)
    {
        // pathname NUL length SP mtime(OCT) SP mode(OCT) SP serial(OCT)
        var nul = Array.IndexOf(info, (byte)0);
        var name = nul > 0 ? Encoding.UTF8.GetString(info, 0, nul) : Encoding.UTF8.GetString(info);
        var rest = nul > 0 && nul + 1 < info.Length ? Encoding.UTF8.GetString(info, nul + 1, info.Length - nul - 1) : "";
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        long length = 0;
        if (parts.Length > 0) long.TryParse(parts[0], out length);
        _fileLength = length;

        var safe = SafeLeafName(name);
        _fileName = safe;
        _destPath = UniquePath(Path.Combine(_downloadDir, safe));
        _filePos = 0;
        _discard = false;

        try
        {
            Directory.CreateDirectory(_downloadDir);
            _file = new FileStream(_destPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        }
        catch (Exception ex)
        {
            _file = null;
            Notice?.Invoke("Could not create " + _destPath + ": " + ex.Message);
            SendHeader(ZSKIP);
            return;
        }

        Notice?.Invoke($"Receiving {safe}" + (length > 0 ? $" ({length:N0} bytes)" : "") + " over ZMODEM.");
        Changed?.Invoke(this);
        SendHeader(ZRPOS, 0);
    }

    private void EndFile(long declared)
    {
        var received = _filePos;
        _file?.Flush();
        _file?.Dispose();
        _file = null;

        if (declared != received)
        {
            // Disk short-write or a lost tail: ask the sender to resume at what we hold.
            _file = received > 0 && _destPath is not null
                ? new FileStream(_destPath, FileMode.Open, FileAccess.Write, FileShare.Read)
                : null;
            _file?.Seek(received, SeekOrigin.Begin);
            _filePos = received;
            SendHeader(ZRPOS, (uint)received);
            return;
        }

        _filesReceived++;
        SavedPath = _destPath;
        Notice?.Invoke($"Saved {_destPath} ({received:N0} bytes).");
        Changed?.Invoke(this);
        SendRinit();                                  // ready for the next file
    }

    // ---------------------------------------------------------------- sending

    private void SendZfile()
    {
        _txStarted = true;
        WriteBinaryHeader(ZFILE, 0, _txCrc32);
        var info = Encoding.UTF8.GetBytes(_txName + "\0" + _txLength + " " +
                                          Convert.ToString((long)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 8) + " 100644 0");
        SendSubpacket(info, 0, info.Length, ZCRCW);
    }

    private void SendDataHeader(uint position)
    {
        _txStreaming = true;
        WriteBinaryHeader(ZDATA, position, _txCrc32);
        SendDataFrames();
    }

    /// <summary>Sends one window per call so the session thread stays responsive to headers.</summary>
    private void SendDataFrames()
    {
        const long budget = 1L << 20;   // one megabyte per visit
        var spent = 0L;

        while (_txSent < _txLength && spent < budget)
        {
            var left = (int)Math.Min(SubpacketSize, _txLength - _txSent);
            var end = _txSent + left >= _txLength ? ZCRCE : ZCRCG;
            SendSubpacket(_txData, (int)_txSent, left, end);
            _txSent += left;
            spent += left;
            _lastProgress = DateTime.UtcNow;
            _lastSend = DateTime.UtcNow;
            Changed?.Invoke(this);
        }

        if (_txSent >= _txLength)
        {
            _txStreaming = false;
            SendHeader(ZEOF, (uint)_txLength);
            _txAwaitEofAck = true;
        }
        else _wake.Set();   // come straight back for the rest of the file
    }

    private void StartFinish()
    {
        SendHeader(ZFIN);
        _lastSend = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------- frame writing

    private void SendRinit()
    {
        // CANFDX | CANOVIO, buffer 0 (nonstop), in ZF0 (the last data byte).
        // CANFC32 is deliberately not offered: lrzsz's sz then uses 16 bit CRCs, the path
        // its own headers let us verify byte for byte. The 32 bit code is still here for a
        // peer that insists, but this build never negotiates it.
        var flags = new uint[4];
        flags[3] = CANFDX | CANOVIO;
        WriteHeader(ZRINIT, 0, flags);
        _lastSend = DateTime.UtcNow;
    }

    private void SendHeader(int type, uint value = 0)
    {
        var flags = new uint[4];
        flags[0] = value & 0xFF;
        flags[1] = (value >> 8) & 0xFF;
        flags[2] = (value >> 16) & 0xFF;
        flags[3] = (value >> 24) & 0xFF;
        WriteHeader(type, value, flags);
        _lastSend = DateTime.UtcNow;
    }

    /// <summary>
    /// Responses go out as hex headers: the reverse channel stays free of control
    /// characters, which is what lets a receiver interrupt a streaming sender.
    /// </summary>
    private void WriteHeader(int type, uint value, uint[] flags)
    {
        var bytes = new List<byte>(32) { ZPAD, ZPAD, ZDLE, ZHEX };
        var payload = new byte[5];
        payload[0] = (byte)type;
        for (var i = 0; i < 4; i++) payload[1 + i] = (byte)flags[i];
        var crc = Crc16(payload, 0, 5);

        for (var i = 0; i < 5; i++) AppendHex(bytes, payload[i]);
        AppendHex(bytes, (byte)(crc >> 8));          // high byte first, as lrzsz and XMODEM do
        AppendHex(bytes, (byte)(crc & 0xFF));
        bytes.Add(0x0D);
        if (type != ZACK && type != ZFIN)
        {
            bytes.Add(0x0A);
            bytes.Add(0x11);   // the XON releases a sender stuck behind line-noise XOFF
        }
        _write(bytes.ToArray());
    }

    private void SendSubpacket(byte[] data, int offset, int count, byte frameEnd)
    {
        var outBytes = new List<byte>(count + 16);
        for (var i = 0; i < count; i++)
        {
            var b = data[offset + i];
            if (NeedsEscape(b)) { outBytes.Add(ZDLE); outBytes.Add((byte)(b ^ 0x40)); }
            else outBytes.Add(b);
        }
        outBytes.Add(ZDLE);
        outBytes.Add(frameEnd);

        var span = new byte[count];
        Buffer.BlockCopy(data, offset, span, 0, count);
        if (_txCrc32)
        {
            var crc = Crc32WithEnd(span, frameEnd);
            for (var i = 0; i < 4; i++) AppendEscaped(outBytes, (byte)((crc >> (8 * i)) & 0xFF));
        }
        else
        {
            var crc = Crc16WithEnd(span, frameEnd);
            AppendEscaped(outBytes, (byte)(crc >> 8));
            AppendEscaped(outBytes, (byte)(crc & 0xFF));
        }
        _write(outBytes.ToArray());
    }

    /// <summary>
    /// A binary header for a frame that carries data subpackets. The header kind has to
    /// agree with the subpacket CRC: a hex header always means CRC-16 data, so a 32 bit
    /// transfer must announce itself with ZBIN32 or the receiver checks the wrong sum.
    /// </summary>
    private void WriteBinaryHeader(int type, uint value, bool crc32)
    {
        var outBytes = new List<byte>(20) { ZPAD, ZDLE, crc32 ? ZBIN32 : ZBIN };
        var payload = new byte[5];
        payload[0] = (byte)type;
        payload[1] = (byte)(value & 0xFF);
        payload[2] = (byte)((value >> 8) & 0xFF);
        payload[3] = (byte)((value >> 16) & 0xFF);
        payload[4] = (byte)((value >> 24) & 0xFF);

        foreach (var b in payload) AppendEscaped(outBytes, b);

        if (crc32)
        {
            var crc = Crc32(payload, 0, 5);
            for (var i = 0; i < 4; i++) AppendEscaped(outBytes, (byte)((crc >> (8 * i)) & 0xFF));
        }
        else
        {
            var crc = Crc16(payload, 0, 5);
            AppendEscaped(outBytes, (byte)(crc >> 8));
            AppendEscaped(outBytes, (byte)(crc & 0xFF));
        }
        _write(outBytes.ToArray());
    }

    private void SendText(string text)
    {
        try { _write(Encoding.ASCII.GetBytes(text)); } catch { }
    }

    // ---------------------------------------------------------------- timers

    private void Pump()
    {
        if (_role != ZmodemRole.Send) return;
        if (!_txStarted) return;
        if (_txAwaitEofAck) return;
        if (!_txStreaming) return;   // a ZACK for a metadata frame is not a data stream
        if (_txSent < _txLength) SendDataFrames();   // a ZACK is a nudge, not a prerequisite
    }

    private void CheckTimeouts()
    {
        var now = DateTime.UtcNow;

        // Nudge a silent peer: a receiver re-offers ZRINIT, a sender re-offers the file.
        if ((now - _lastSend).TotalSeconds >= 2.0)
        {
            if (_role == ZmodemRole.Receive && _mode == Mode.Hunt) SendRinit();
            else if (_role == ZmodemRole.Send && !_txStarted) SendHeader(ZRQINIT);
            else if (_role == ZmodemRole.Send && _txAwaitEofAck) StartFinish();
            _lastSend = now;
        }

        if ((now - _lastProgress).TotalSeconds > 90)
            Finish(false, "ZMODEM stalled: no progress for 90 seconds.");
        else if ((now - _started).TotalSeconds > 1800)
            Finish(false, "ZMODEM session ran past its time limit.");
    }

    private void Finish(bool ok, string message)
    {
        if (Finished) return;
        Finished = true;
        Failed = !ok;
        Message = message;
        try { _file?.Flush(); _file?.Dispose(); } catch { }
        _file = null;
        _mode = Mode.Finished;
        Changed?.Invoke(this);
        Completed?.Invoke(ok, message);
    }

    private string Summarise()
    {
        if (_role == ZmodemRole.Send) return "Sent " + _txName + " (" + _txLength + " bytes).";
        var where = SavedPath ?? _destPath ?? "(unknown)";
        return _filesReceived <= 1
            ? $"Saved {where} ({_filePos:N0} bytes)."
            : $"Saved {_filesReceived} files, last {where}.";
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The text-side signature: ZPAD ZDLE ZHEX, i.e. the start of a hex header.</summary>
    public static int FindStart(byte[] data, int count)
    {
        for (var i = 0; i + 2 < count; i++)
        {
            if (data[i] == ZPAD && data[i + 1] == ZDLE &&
                (data[i + 2] == ZHEX || data[i + 2] == ZBIN || data[i + 2] == ZBIN32))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// One leaf file name, no directories, no device names, no traversal. A peer that
    /// offers "..\..\windows\x" gets "x"; one that offers nothing usable gets "zmodem.bin".
    /// </summary>
    public static string SafeLeafName(string? offered)
    {
        var name = offered ?? "";
        name = name.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name.Substring(slash + 1);
        name = name.Trim().Trim('"');
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (name.Length == 0 || name == "." || name == "..") name = "zmodem.bin";

        var stem = Path.GetFileNameWithoutExtension(name);
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4",
                               "LPT1", "LPT2", "LPT3", "COM5", "COM6", "COM7", "COM8", "COM9" };
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            name = "_" + name;

        return name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")" + ext);
    }

    private void Trim()
    {
        if (_pos > 1 << 16)
        {
            _buffer.RemoveRange(0, _pos);
            _pos = 0;
        }
    }

    private static bool NeedsEscape(byte b) =>
        b == ZDLE || b < 0x20 || b == 0x90 || b == 0x91 || b == 0x93;

    private static void AppendEscaped(List<byte> target, byte b)
    {
        if (NeedsEscape(b)) { target.Add(ZDLE); target.Add((byte)(b ^ 0x40)); }
        else target.Add(b);
    }

    private static void AppendHex(List<byte> target, byte b)
    {
        const string digits = "0123456789abcdef";   // upper case false-triggers X/YMODEM
        target.Add((byte)digits[(b >> 4) & 0xF]);
        target.Add((byte)digits[b & 0xF]);
    }

    private static int HexValue(byte c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;   // tolerate upper case on receive
        return -1;
    }

    private static ReadOnlySpan<byte> CollectionsMarshalSpan(List<byte> list)
    {
        var array = list.ToArray();
        return array;
    }

    public static ushort Crc16(byte[] data, int offset, int count)
    {
        ushort crc = 0;
        for (var i = 0; i < count; i++)
        {
            crc ^= (ushort)(data[offset + i] << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private static ushort Crc16WithEnd(ReadOnlySpan<byte> data, byte frameEnd)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        crc ^= (ushort)(frameEnd << 8);
        for (var bit = 0; bit < 8; bit++)
            crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        return crc;
    }

    /// <summary>CRC-32 as ZMODEM uses it: preset -1, final inversion.</summary>
    public static uint Crc32(byte[] data, int offset, int count)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < count; i++)
        {
            crc ^= (uint)data[offset + i] << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
        }
        return ~crc;
    }

    private static uint Crc32WithEnd(ReadOnlySpan<byte> data, byte frameEnd)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
        }
        crc ^= (uint)frameEnd << 24;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
        return ~crc;
    }

    private static string Name(int type) => type switch
    {
        ZRQINIT => "ZRQINIT", ZRINIT => "ZRINIT", ZSINIT => "ZSINIT", ZACK => "ZACK",
        ZFILE => "ZFILE", ZSKIP => "ZSKIP", ZNAK => "ZNAK", ZABORT => "ZABORT",
        ZFIN => "ZFIN", ZRPOS => "ZRPOS", ZDATA => "ZDATA", ZEOF => "ZEOF",
        ZFERR => "ZFERR", ZCRC => "ZCRC", ZCHALLENGE => "ZCHALLENGE", ZCOMPL => "ZCOMPL",
        ZCAN => "ZCAN", ZFREECNT => "ZFREECNT", ZCOMMAND => "ZCOMMAND",
        _ => "frame " + type
    };
}

public enum ZmodemRole { Receive, Send }
