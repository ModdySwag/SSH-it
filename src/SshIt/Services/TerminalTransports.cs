using System.IO.Ports;
using System.Net.Sockets;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// A non-OpenSSH byte pipe: the terminal pane only needs these members, so a telnet socket and a
/// serial port can drive the same parser and renderer as a pseudo console.
/// </summary>
public interface ITerminalTransport : IDisposable
{
    event Action<byte[], int>? DataReceived;
    event Action<int>? Exited;
    bool IsRunning { get; }
    void Write(byte[] data);
    void Resize(int columns, int rows);
}

/// <summary>
/// Raw TCP with the minimal telnet option negotiation a terminal server expects. Option traffic
/// is answered (WILL/DO for the handful we support, WONT/DONT for the rest) and stripped before
/// the bytes reach the VT parser; NAWS keeps the remote program the same size as the pane.
/// </summary>
public sealed class TelnetTransport : ITerminalTransport
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    private const byte OPT_ECHO = 1, OPT_SGA = 3, OPT_TTYPE = 24, OPT_NAWS = 31;
    private const byte TTYPE_IS = 0, TTYPE_SEND = 1;

    private readonly TcpClient _client = new();
    private NetworkStream? _stream;
    private Thread? _reader;
    private volatile bool _running;
    private int _columns = 80, _rows = 24;

    public event Action<byte[], int>? DataReceived;
    public event Action<int>? Exited;
    public bool IsRunning => _running;

    public void Start(string host, int port, int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        _client.Connect(host, port <= 0 ? 23 : port);
        _client.NoDelay = true;
        _stream = _client.GetStream();
        _running = true;

        Send(new[] { IAC, WILL, OPT_TTYPE, IAC, WILL, OPT_NAWS, IAC, WILL, OPT_SGA });

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "telnet-read" };
        _reader.Start();
    }

    public void Write(byte[] data)
    {
        if (_running) Send(data);
    }

    public void Resize(int columns, int rows)
    {
        _columns = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        if (!_running) return;

        // NAWS: two 16-bit big-endian values inside a subnegotiation.
        Send(new[]
        {
            IAC, SB, OPT_NAWS,
            (byte)(_columns >> 8), (byte)(_columns & 0xFF),
            (byte)(_rows >> 8), (byte)(_rows & 0xFF),
            IAC, SE
        });
    }

    private void Send(byte[] data)
    {
        try
        {
            var stream = _stream;
            if (stream is null) return;
            lock (_client) stream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log.Warn("telnet write: " + ex.Message);
            Stop(ex.HResult);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        var filter = new TelnetFilter();
        try
        {
            while (_running)
            {
                var read = _stream!.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                var clean = filter.Process(buffer, read, out var reply);
                if (reply.Count > 0) Send(reply.ToArray());
                if (clean.Count > 0) DataReceived?.Invoke(clean.ToArray(), clean.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("telnet read: " + ex.Message);
        }
        finally
        {
            Stop(0);
        }
    }

    private void Stop(int code)
    {
        if (!_running) return;
        _running = false;
        try { _client.Close(); } catch { }
        Exited?.Invoke(code);
    }

    public void Dispose()
    {
        _running = false;
        try { _client.Close(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _client.Dispose();
    }

    /// <summary>
    /// Strips IAC sequences from a byte stream, answering the options a terminal asks for. Kept
    /// separate from the socket so the state machine can be tested without a server.
    /// </summary>
    public sealed class TelnetFilter
    {
        private enum State { Data, Iac, Negotiation, Subnegotiation, SubnegotiationIac }

        private State _state = State.Data;
        private byte _verb;

        /// <summary>Feeds a chunk; returns the bytes that belong to the terminal and any reply.</summary>
        public List<byte> Process(byte[] data, int count, out List<byte> reply)
        {
            var clean = new List<byte>(count);
            reply = new List<byte>();

            for (var i = 0; i < count; i++)
            {
                var b = data[i];
                switch (_state)
                {
                    case State.Data:
                        if (b == IAC) _state = State.Iac;
                        else clean.Add(b);
                        break;

                    case State.Iac:
                        if (b == IAC) { clean.Add(IAC); _state = State.Data; }   // escaped 0xFF
                        else if (b is DO or DONT or WILL or WONT) { _verb = b; _state = State.Negotiation; }
                        else if (b == SB) _state = State.Subnegotiation;
                        else _state = State.Data;
                        break;

                    case State.Negotiation:
                        _state = State.Data;
                        Answer(b, reply);
                        break;

                    case State.Subnegotiation:
                        if (b == IAC) _state = State.SubnegotiationIac;
                        break;

                    case State.SubnegotiationIac:
                        if (b == SE) { _state = State.Data; break; }
                        if (b == IAC) { _state = State.Subnegotiation; break; }
                        // TTYPE SEND asks us to name the terminal.
                        if (_verb == 0 && b == TTYPE_SEND) { }
                        _state = State.Subnegotiation;
                        break;
                }
            }

            return clean;
        }

        private void Answer(byte option, List<byte> reply)
        {
            var supported = option is OPT_SGA or OPT_TTYPE or OPT_NAWS;

            if (_verb == DO)
            {
                reply.Add(IAC);
                reply.Add(supported ? WILL : WONT);
                reply.Add(option);
            }
            else if (_verb == WILL)
            {
                reply.Add(IAC);
                reply.Add(option == OPT_ECHO || option == OPT_SGA ? DO : DONT);
                reply.Add(option);
            }
            // DONT / WONT need no answer; ignoring them prevents negotiation loops.
        }
    }
}

/// <summary>
/// A serial port as a terminal transport: 8N1 by default, no echo of our own, bytes straight to
/// the parser. Resize is a no-op because a serial line has no window size.
/// </summary>
public sealed class SerialTransport : ITerminalTransport
{
    private SerialPort? _port;
    private volatile bool _running;

    public event Action<byte[], int>? DataReceived;
    public event Action<int>? Exited;
    public bool IsRunning => _running;

    public void Start(string portName, int baud, int dataBits, string parity, string stopBits,
                      string flowControl, int readTimeoutMs = 100)
    {
        var port = new SerialPort(portName, baud <= 0 ? 9600 : baud,
                                  ParseParity(parity), dataBits is >= 5 and <= 8 ? dataBits : 8,
                                  ParseStopBits(stopBits))
        {
            Handshake = ParseHandshake(flowControl),
            ReadTimeout = readTimeoutMs,
            WriteTimeout = readTimeoutMs,
            DtrEnable = true,
            RtsEnable = true,
        };

        port.DataReceived += (_, _) =>
        {
            try
            {
                var available = port.BytesToRead;
                if (available <= 0) return;
                var buffer = new byte[available];
                var read = port.Read(buffer, 0, available);
                if (read > 0) DataReceived?.Invoke(buffer, read);
            }
            catch (Exception ex)
            {
                Log.Warn("serial read: " + ex.Message);
            }
        };

        port.Open();
        _port = port;
        _running = true;
    }

    public void Write(byte[] data)
    {
        try
        {
            if (_port is { IsOpen: true }) _port.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log.Warn("serial write: " + ex.Message);
            _running = false;
            Exited?.Invoke(ex.HResult);
        }
    }

    public void Resize(int columns, int rows) { }

    public void Dispose()
    {
        _running = false;
        try { _port?.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;
    }

    /// <summary>Validated from the session so a typo shows up as a clear message, not an exception.</summary>
    public static string Describe(string? portName, int baud, int dataBits, string? parity, string? stopBits)
        => $"{portName} {baud} {dataBits}{ShortParity(parity)}{ShortStop(stopBits)}";

    public static string ShortParity(string? parity) => parity switch
    {
        "Even" => "E", "Odd" => "O", "Mark" => "M", "Space" => "S", _ => "N"
    };

    public static string ShortStop(string? stopBits) => stopBits switch
    {
        "Two" => "2", "OnePointFive" => "1.5", _ => "1"
    };

    private static Parity ParseParity(string? parity) => parity switch
    {
        "Even" => Parity.Even, "Odd" => Parity.Odd, "Mark" => Parity.Mark,
        "Space" => Parity.Space, _ => Parity.None
    };

    private static StopBits ParseStopBits(string? stopBits) => stopBits switch
    {
        "Two" => StopBits.Two, "OnePointFive" => StopBits.OnePointFive, _ => StopBits.One
    };

    private static Handshake ParseHandshake(string? flow) => flow switch
    {
        "XOnXOff" => Handshake.XOnXOff, "RequestToSend" => Handshake.RequestToSend,
        "RequestToSendXOnXOff" => Handshake.RequestToSendXOnXOff, _ => Handshake.None
    };
}
