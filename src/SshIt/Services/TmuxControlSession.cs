using System.Diagnostics;
using System.IO;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// A tmux control-mode connection over a RAW pipe. Not ConPTY, not the VT parser: `ssh -tt host
/// tmux -CC attach` writes escape sequences (`\033P1000p`) that a console host eats, and control mode
/// is a text protocol that has to arrive byte-for-byte.
///
/// The process is started with redirected stdin/stdout; a background thread splits the stream into
/// lines, feeds <see cref="TmuxControlParser"/> and publishes the events. Commands go out the same
/// pipe, one line each.
/// </summary>
public sealed class TmuxControlSession : IDisposable
{
    private readonly Process _process;
    private readonly Action<TmuxEvent>? _onEvent;
    private readonly TmuxControlParser _parser = new();
    private readonly List<TmuxEvent> _events = new();
    private readonly object _gate = new();
    private readonly Thread _reader;
    private readonly Thread _errorReader;
    private volatile bool _stopping;

    /// <summary>Every parsed event, published on the reader thread. Subscribe before the first read.</summary>
    public event Action<TmuxEvent>? Received;

    /// <summary>The stream ended (child exited or closed its stdout). Raised on the reader thread.</summary>
    public event Action? Closed;

    private readonly Queue<string> _stderr = new();
    private readonly object _stderrGate = new();

    /// <summary>The child's last stderr lines, for an honest error when ssh itself failed.</summary>
    public string StderrTail
    {
        get { lock (_stderrGate) return string.Join(" ", _stderr); }
    }

    private TmuxControlSession(Process process, Action<TmuxEvent>? onEvent)
    {
        _process = process;
        _onEvent = onEvent;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "tmux-control" };

        // A redirected stderr nobody reads fills its pipe and blocks the child. wsl.exe in
        // particular warns on stderr before tmux has said anything at all.
        _errorReader = new Thread(ReadErrors) { IsBackground = true, Name = "tmux-stderr" };
        _errorReader.Start();
    }

    private bool _begun;

    /// <summary>
    /// Starts reading. Separate from the constructor because the stream can deliver a event before
    /// the caller has had a chance to subscribe — the first lines are already in the pipe.
    /// </summary>
    public void Begin()
    {
        if (_begun) return;
        _begun = true;
        _reader.Start();
    }

    public static TmuxControlSession Start(ProcessStartInfo psi, Action<TmuxEvent>? onEvent = null)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("the tmux client could not be started");
        return new TmuxControlSession(process, onEvent);
    }

    /// <summary>One command per line. Control mode does not want a shell to interpret it.</summary>
    public void Send(string command)
    {
        var bytes = Encoding.UTF8.GetBytes(command + "\n");
        _process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        _process.StandardInput.BaseStream.Flush();
    }

    public List<TmuxEvent> Snapshot()
    {
        lock (_gate) return new List<TmuxEvent>(_events);
    }

    /// <summary>How many events have arrived; a caller keeps this as a cursor before sending.</summary>
    public int Count { get { lock (_gate) return _events.Count; } }

    /// <summary>
    /// Polls events AT OR AFTER fromIndex. The cursor matters: a session name or an earlier response
    /// can satisfy a loose match, and "wait for the answer to THIS command" must not read an old one.
    /// </summary>
    public bool WaitFor(Func<TmuxEvent, bool> match, int timeoutMs, out TmuxEvent? hit, int fromIndex = 0)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_gate)
            {
                hit = null;
                for (var i = Math.Clamp(fromIndex, 0, _events.Count); i < _events.Count; i++)
                {
                    if (!match(_events[i])) continue;
                    hit = _events[i];
                    break;
                }
            }
            if (hit is not null) return true;
            if (_stopping) break;
            Thread.Sleep(50);
        }
        hit = null;
        return false;
    }

    private void ReadErrors()
    {
        try
        {
            var stream = _process.StandardError;
            string? line;
            while ((line = stream.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Log.Warn("tmux stderr: " + line);
                lock (_stderrGate)
                {
                    _stderr.Enqueue(line.Trim());
                    while (_stderr.Count > 4) _stderr.Dequeue();
                }
            }
        }
        catch (Exception ex) { Log.Warn("tmux stderr: " + ex.Message); }
    }

    private void ReadLoop()
    {
        var pending = new List<byte>(4096);
        var buffer = new byte[8192];
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (!_stopping)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        pending.Add(buffer[i]);
                        continue;
                    }

                    var line = Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r');
                    pending.Clear();
                    Publish(line);
                }

                // A control-mode stream is line based, but a long %output line can exceed one read;
                // the pending buffer carries it across reads until the newline arrives.
            }
        }
        catch (Exception ex)
        {
            Log.Warn("tmux control: " + ex.Message);
        }
        finally
        {
            if (pending.Count > 0) Publish(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'));
            try { Closed?.Invoke(); }
            catch (Exception ex) { Log.Warn("tmux closed subscriber: " + ex.Message); }
        }
    }

    private void Publish(string line)
    {
        TmuxEvent? produced;
        try { produced = _parser.FeedLine(line); }
        catch (Exception ex) { Log.Warn("tmux control parse: " + ex.Message); return; }
        if (produced is null) return;
        lock (_gate) _events.Add(produced);
        _onEvent?.Invoke(produced);
        try { Received?.Invoke(produced); }
        catch (Exception ex) { Log.Warn("tmux event subscriber: " + ex.Message); }
    }

    public bool Running => !_stopping && _process is { HasExited: false };

    public void Dispose()
    {
        _stopping = true;
        try
        {
            if (!_process.HasExited)
            {
                try { Send(TmuxControlParser.Detach); } catch { }
                if (!_process.WaitForExit(1500)) _process.Kill(true);
            }
        }
        catch (Exception ex) { Log.Warn("tmux control dispose: " + ex.Message); }
        try { _reader.Join(1000); } catch { }
        try { _errorReader.Join(200); } catch { }
        _process.Dispose();
    }
}
