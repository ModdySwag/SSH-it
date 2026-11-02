using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// Hosts a child process (normally ssh.exe) inside a Windows pseudo console.
/// This is the same mechanism Windows Terminal uses: the child believes it is
/// talking to a real terminal, so remote PTY allocation, colours and resize all work.
///
/// The pipe handles belong to this object alone and are driven with ReadFile/WriteFile.
/// Never wrap them in FileStream: FileStream takes ownership of the handle it is handed
/// and closes it when disposed, which EOFs the pseudo console and kills the child with
/// STATUS_CONTROL_C_EXIT (0xC000013A) on the next keystroke.
/// </summary>
public sealed class ConPty : IDisposable
{
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;


    private const int ErrorInvalidHandle = 6;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const int ErrorPipeNotConnected = 233;
    private const int ErrorOperationAborted = 995;

    private IntPtr _pseudoConsole;
    private IntPtr _attrList;
    private IntPtr _inputHandle;   // keystrokes go here
    private IntPtr _outputHandle;  // the child's screen output comes from here
    private Process? _process;
    private Thread? _reader;
    private volatile bool _disposed;

    private readonly object _writeGate = new();
    private readonly byte[] _writeBuffer = new byte[4096];

    public event Action<byte[], int>? DataReceived;
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    /// <summary>
    /// When the pty child started, UTC. Paired with <see cref="ProcessId"/> this identifies a live
    /// pty across pid reuse — what an agent approval is bound to.
    /// </summary>
    public DateTime StartedAtUtc { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public DateTime StartedAt { get; } = DateTime.Now;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 17763;

    public void Start(string executable, string arguments, short columns, short rows,
                      string? workingDirectory = null, IDictionary<string, string>? extraEnv = null)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Windows 10 1809 or newer is required for the pseudo console.");

        CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0);
        CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0);

        var hr = CreatePseudoConsole(new COORD { X = columns, Y = rows }, inRead, outWrite, 0, out _pseudoConsole);
        if (hr != 0)
        {
            CloseHandle(inRead);
            CloseHandle(inWrite);
            CloseHandle(outRead);
            CloseHandle(outWrite);
            throw new Win32Exception(hr, "CreatePseudoConsole failed.");
        }

        // The pseudo console owns its two ends now; the other two stay with us.
        CloseHandle(inRead);
        CloseHandle(outWrite);

        _inputHandle = inWrite;
        _outputHandle = outRead;

        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startupInfo.StartupInfo.dwFlags = 0;

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size))
        {
            var listError = Marshal.GetLastWin32Error();
            Cleanup();
            throw new Win32Exception(listError, "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(_attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                                       _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            var attrError = Marshal.GetLastWin32Error();
            Cleanup();
            throw new Win32Exception(attrError, "UpdateProcThreadAttribute failed.");
        }

        startupInfo.lpAttributeList = _attrList;

        var env = BuildEnvironment(extraEnv);
        var commandLine = new StringBuilder(Quote(executable) + " " + arguments);

        var ok = CreateProcessW(
            executable,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            env,
            workingDirectory,
            ref startupInfo,
            out var pi);

        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(env);
            Cleanup();
            throw new Win32Exception(err, "Could not start " + executable);
        }

        Marshal.FreeHGlobal(env);
        CloseHandle(pi.hThread);
        ProcessId = pi.dwProcessId;
        Log.Info($"pty start pid={ProcessId} {Path.GetFileName(executable)} {arguments}");

        _process = Process.GetProcessById(pi.dwProcessId);
        try { StartedAtUtc = _process.StartTime.ToUniversalTime(); }
        catch (Exception ex) { Log.Warn("pty: start time unreadable: " + ex.Message); }
        _process.EnableRaisingEvents = true;
        // The read loop also raises Exited(-1) on EOF. Raising twice turned a clean `exit` into a
        // failure (the second callback saw the same generation) and scheduled a reconnect.
        _process.Exited += (_, _) => RaiseExited(SafeExitCode());
        CloseHandle(pi.hProcess);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-reader" };
        _reader.Start();
    }


    private int _exitRaised;

    /// <summary>The first exit wins; the read loop's EOF and the process event race on purpose.</summary>
    private void RaiseExited(int code)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        Exited?.Invoke(code);
    }

    private int SafeExitCode()
    {
        try { return _process is { HasExited: true } p ? p.ExitCode : -1; }
        catch { return -1; }
    }

    private void ReadLoop()
    {
        var buffer = new byte[32768];
        try
        {
            while (!_disposed)
            {
                if (!ReadFile(_outputHandle, buffer, buffer.Length, out var read, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (!_disposed && err != ErrorBrokenPipe && err != ErrorNoData
                        && err != ErrorInvalidHandle && err != ErrorOperationAborted)
                        Log.Warn($"pty read failed err={err}");
                    break;
                }
                if (read <= 0) break;
                DataReceived?.Invoke(buffer, read);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (Exception ex) { Log.Error(ex, "conpty read"); }
        finally
        {
            if (!_disposed) RaiseExited(-1);
        }
    }

    /// <summary>Writes raw bytes into the child's input. Safe to call from any thread.</summary>
    public void Write(byte[] data)
    {
        if (data is null || data.Length == 0 || _disposed) return;

        lock (_writeGate)
        {
            var handle = _inputHandle;
            if (handle == IntPtr.Zero) return;

            var offset = 0;
            while (offset < data.Length)
            {
                var remaining = data.Length - offset;
                var chunk = Math.Min(remaining, _writeBuffer.Length);
                var source = data;

                // Writes are normally a few bytes; only large pastes take the copy path.
                if (offset != 0 || chunk != data.Length)
                {
                    Buffer.BlockCopy(data, offset, _writeBuffer, 0, chunk);
                    source = _writeBuffer;
                }

                if (!WriteFile(handle, source, chunk, out var written, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (!_disposed && err != ErrorBrokenPipe && err != ErrorNoData
                        && err != ErrorPipeNotConnected && err != ErrorInvalidHandle)
                        Log.Warn($"pty write failed err={err}");
                    return;
                }
                if (written <= 0) return;
                offset += written;
            }
        }
    }

    /// <summary>
    /// True while the pty input pipe is still open. The self-test uses this to prove that a
    /// write did not take the handle down with it (the old FileStream-per-write did exactly
    /// that, and the child died with 0xC000013A on the first keystroke).
    /// </summary>
    public bool InputHandleIsOpen()
    {
        var handle = _inputHandle;
        if (handle == IntPtr.Zero) return false;
        return GetHandleInformation(handle, out _);
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(short columns, short rows)
    {
        if (_pseudoConsole == IntPtr.Zero || columns <= 0 || rows <= 0) return;
        ResizePseudoConsole(_pseudoConsole, new COORD { X = columns, Y = rows });
    }

    private static IntPtr BuildEnvironment(IDictionary<string, string>? extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                vars[key] = value;
        }
        if (extra is not null)
            foreach (var pair in extra)
                vars[pair.Key] = pair.Value;

        var builder = new StringBuilder();
        foreach (var pair in vars)
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        builder.Append('\0');

        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private void Cleanup()
    {
        if (_attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
        if (_inputHandle != IntPtr.Zero)
        {
            CloseHandle(_inputHandle);
            _inputHandle = IntPtr.Zero;
        }
        if (_outputHandle != IntPtr.Zero)
        {
            CloseHandle(_outputHandle);
            _outputHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch { }

        Cleanup();
        _process?.Dispose();
    }

    // ---------------- interop ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);
}
