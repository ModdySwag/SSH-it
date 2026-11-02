using System.IO;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// Headless ZMODEM exercise: opens a real pseudo console on a saved session, types a
/// command (normally "sz …" or "rz"), and runs the same ZmodemSession the terminal uses,
/// with no window and no dispatcher. This is the regression rig for the protocol code.
///
///   SSH-it.exe --zmodem-test recv --session ID --command "sz /etc/hostname" [--out DIR]
///   SSH-it.exe --zmodem-test send --session ID --command "rz -y" --file C:\path [--out DIR]
///
/// Prints one summary line per file and exits 0 when the transfer completed, 1 otherwise.
/// </summary>
public static class ZmodemProbe
{
    /// <summary>File.Exists is true for a file this process cannot open; opening is the real test.</summary>
    private static bool CanRead(string path)
    {
        try { using var probe = File.OpenRead(path); return true; }
        catch { return false; }
    }

    public static bool Wants(string[] args) =>
        args.Any(a => a.Equals("--zmodem-test", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        // App.OnStartup calls this BEFORE it wires the unhandled-exception handlers, so anything that
        // escapes here is a WPF crash dump and an 0xE0434352 exit. Every mode gets a sentence instead.
        try { return RunCore(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("zmodem test failed: " + ex.Message);
            return 2;
        }
    }

    private static int RunCore(string[] args)
    {
        var mode = Value(args, "--zmodem-test") ?? "";
        var sessionId = Value(args, "--session");
        var command = Command(args);
        var outDir = Value(args, "--out");
        var file = Value(args, "--file");
        var timeout = int.TryParse(Value(args, "--timeout"), out var t) ? t : 180;

        if (mode == "loop") return Loopback(args, outDir, file);
        if (mode == "pipe") return PipeTest(args, outDir, file);

        if (sessionId is null || command is null || (mode != "recv" && mode != "send"))
        {
            Console.Error.WriteLine("usage: --zmodem-test recv|send|loop [--session ID] [--command \"sz file\"] [--out DIR] [--file PATH]");
            return 2;
        }

        // A sender with nothing to send used to reach ZmodemSession.Arm, whose FileNotFoundException
        // escaped OnStartup as a dispatcher stack trace and an 0xE0434352 exit code.
        if (mode is "send" or "pipe" or "loop"
            && (string.IsNullOrWhiteSpace(file) || !CanRead(file)))
        {
            Console.Error.WriteLine("--zmodem-test " + mode + " needs --file <path> naming a readable file"
                                    + (string.IsNullOrWhiteSpace(file) ? "" : ": " + file));
            return 2;
        }

        outDir ??= Path.Combine(Path.GetTempPath(), "sshit-zmodem");
        Directory.CreateDirectory(outDir);

        var store = new DataStore();
        store.Load();
        var session = store.Sessions.FirstOrDefault(s =>
                          s.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) ??
                      store.Sessions.FirstOrDefault(s =>
                          s.Name.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            Console.Error.WriteLine("no such session: " + sessionId);
            return 2;
        }

        var role = mode == "send" ? ZmodemRole.Send : ZmodemRole.Receive;
        var done = new ManualResetEventSlim(false);
        var sawOutput = new ManualResetEventSlim(false);
        var gate = new object();
        ZmodemSession? transfer = null;
        var exit = 1;

        var pty = new ConPty();
        pty.DataReceived += (data, count) =>
        {
            sawOutput.Set();
            lock (gate)
            {
                if (transfer is null)
                {
                    if (role == ZmodemRole.Send) return;      // sender waits for its own header
                    var start = ZmodemSession.FindStart(data, count);
                    if (start < 0) return;
                    transfer = Arm(role, pty, outDir, file, (ok, text) =>
                    {
                        exit = ok ? 0 : 1;
                        Console.WriteLine((ok ? "OK   " : "FAIL ") + text);
                        done.Set();
                    });
                    transfer!.Feed(data, start, count - start);
                    return;
                }
                transfer.Feed(data, count);
            }
        };
        pty.Exited += code => { Console.WriteLine("pty exited " + code); done.Set(); };

        if (role == ZmodemRole.Send)
        {
            // Arm before typing: rz's first ZRQINIT must find a sender already waiting.
            transfer = Arm(role, pty, outDir, file, (ok, text) =>
            {
                exit = ok ? 0 : 1;
                Console.WriteLine((ok ? "OK   " : "FAIL ") + text);
                done.Set();
            });
        }

        var env = SshLauncher.BuildEnvironment(session, null);
        pty.Start(SshLauncher.SshExe, SshLauncher.BuildArguments(session), 120, 40, null, env);

        sawOutput.Wait(TimeSpan.FromSeconds(20));
        Thread.Sleep(1200);
        pty.Write(Encoding.UTF8.GetBytes(command + "\r"));
        Console.WriteLine("typed: " + command);

        // A receiver only learns about the transfer from the far end's first header.
        if (role == ZmodemRole.Receive)
        {
            var armed = false;
            for (var i = 0; i < 25 && !armed; i++)
            {
                Thread.Sleep(1000);
                lock (gate) armed = transfer is not null;
            }
            if (!armed)
            {
                Console.WriteLine("FAIL no ZMODEM header appeared within 25s of typing the command");
                try { pty.Dispose(); } catch { }
                return 1;
            }
        }

        if (!done.Wait(TimeSpan.FromSeconds(timeout)))
        {
            Console.WriteLine("FAIL transfer did not finish within " + timeout + "s");
            transfer?.Abort("timed out");
        }

        Thread.Sleep(400);
        try { pty.Dispose(); } catch { }
        transfer?.Dispose();
        return exit;
    }

    /// <summary>
    /// Two sessions wired back to back, with no host in between. This is the fast rig for
    /// the framing rules: it prints every state change and compares the bytes afterwards.
    /// </summary>
    private static int Loopback(string[] args, string? outDir, string? file)
    {
        var dir = outDir ?? Path.Combine(Path.GetTempPath(), "sshit-zmd-loop-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);

        string src;
        if (file is not null) src = file;
        else
        {
            src = Path.Combine(dir, "payload.bin");
            var payload = new byte[300_000];
            new Random(11).NextBytes(payload);
            for (var i = 0; i < payload.Length; i += 61) payload[i] = 0x18;   // force escaping
            File.WriteAllBytes(src, payload);
        }

        var outFolder = Path.Combine(dir, "out");
        Directory.CreateDirectory(outFolder);
        var done = new ManualResetEventSlim(false);
        var ok = false;

        ZmodemSession? tx = null, rx = null;
        var txWrites = 0;
        var rxWrites = 0;
        tx = new ZmodemSession(ZmodemRole.Send, b =>
        {
            if (txWrites++ < 4) Console.WriteLine("  tx-> " + Convert.ToHexString(b, 0, Math.Min(40, b.Length)));
            rx?.Feed(b, b.Length);
        }, outFolder, src);
        rx = new ZmodemSession(ZmodemRole.Receive, b =>
        {
            if (rxWrites++ < 4) Console.WriteLine("  rx-> " + Convert.ToHexString(b, 0, Math.Min(40, b.Length)));
            tx?.Feed(b, b.Length);
        }, outFolder, null);

        tx.Completed += (good, text) => Console.WriteLine($"tx: {(good ? "ok" : "FAIL")} {text}");
        rx.Completed += (good, text) =>
        {
            ok = good;
            Console.WriteLine($"rx: {(good ? "ok" : "FAIL")} {text}");
            done.Set();
        };
        tx.Changed += z => Console.WriteLine($"tx {z.Bytes}/{z.Total}");
        rx.Changed += z => Console.WriteLine($"rx {z.Bytes}/{z.Total}");

        var finished = done.Wait(TimeSpan.FromSeconds(30));
        tx.Dispose();
        rx.Dispose();

        var received = Path.Combine(outFolder, Path.GetFileName(src));
        var same = File.Exists(received) && File.ReadAllBytes(received).SequenceEqual(File.ReadAllBytes(src));
        Console.WriteLine($"finished={finished} ok={ok} identical={same} path={received}");
        return finished && ok && same ? 0 : 1;
    }

    /// <summary>
    /// The same sessions against a real lrzsz, but over plain pipes to ssh instead of a
    /// pseudo console: "--zmodem-test pipe --session ID --command \"sz /etc/hostname\"".
    /// Pseudo consoles are the product path, but they need a usable local console, which
    /// a headed test harness does not always have; pipes keep this rig honest and fast.
    /// </summary>
    private static int PipeTest(string[] args, string? outDir, string? file)
    {
        var sessionId = Value(args, "--session");
        var command = Command(args);
        if (sessionId is null || command is null)
        {
            Console.Error.WriteLine("usage: --zmodem-test pipe --session ID --command \"sz file\" [--out DIR] [--file PATH]");
            return 2;
        }

        var store = new DataStore();
        store.Load();
        var session = store.Sessions.FirstOrDefault(s => s.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) ??
                      store.Sessions.FirstOrDefault(s => s.Name.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            Console.Error.WriteLine("no such session: " + sessionId);
            return 2;
        }

        outDir ??= Path.Combine(Path.GetTempPath(), "sshit-zmodem");
        Directory.CreateDirectory(outDir);

        var role = file is not null ? ZmodemRole.Send : ZmodemRole.Receive;
        // -tt gives the remote side a pty so sz/rz run in terminal mode. BuildArguments may
        // carry -T, which would cancel that, so it goes.
        var line = ("-tt " + SshLauncher.BuildArguments(session) + " " + SshLauncher.QuoteArgument(command))
                   .Replace(" -T ", " ");

        var info = new System.Diagnostics.ProcessStartInfo(SshLauncher.SshExe, line)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var pair in SshLauncher.BuildEnvironment(session, null)) info.Environment[pair.Key] = pair.Value;

        var done = new ManualResetEventSlim(false);
        ZmodemSession? transfer = null;
        var exit = 1;
        var gate = new object();

        using var process = System.Diagnostics.Process.Start(info)!;
        var stdin = process.StandardInput.BaseStream;
        Console.WriteLine("ssh " + line);

        var errorThread = new Thread(() =>
        {
            try
            {
                string? row;
                while ((row = process.StandardError.ReadLine()) is not null) Console.WriteLine("ssh-err: " + row);
            }
            catch { }
        }) { IsBackground = true };
        errorThread.Start();

        void Arm()
        {
            transfer = new ZmodemSession(role, bytes =>
            {
                try { stdin.Write(bytes, 0, bytes.Length); stdin.Flush(); }
                catch { }
            }, outDir, file);
            transfer.Completed += (ok, text) =>
            {
                exit = ok ? 0 : 1;
                Console.WriteLine((ok ? "OK   " : "FAIL ") + text);
                done.Set();
            };
        }

        if (role == ZmodemRole.Send) Arm();

        var reader = new Thread(() =>
        {
            var buffer = new byte[65536];
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                int read;
                try { read = stream.Read(buffer, 0, buffer.Length); }
                catch (Exception ex) { Console.WriteLine("pipe: read failed " + ex.Message); break; }
                if (read <= 0) break;
                lock (gate)
                {
                    if (transfer is null)
                    {
                        if (role == ZmodemRole.Send) continue;
                        var start = ZmodemSession.FindStart(buffer, read);
                        if (start < 0) continue;
                        Arm();
                        transfer!.Feed(buffer, start, read - start);
                    }
                    else transfer.Feed(buffer, read);
                }
            }
            done.Set();
        }) { IsBackground = true };
        reader.Start();

        if (!done.Wait(TimeSpan.FromSeconds(120)))
        {
            Console.WriteLine("FAIL no completion within 120s");
            exit = 1;
        }
        try { stdin.Flush(); process.StandardInput.Close(); } catch { }
        try { if (!process.WaitForExit(3000)) process.Kill(true); } catch { }
        transfer?.Dispose();
        return exit;
    }

    private static ZmodemSession Arm(ZmodemRole role, ConPty pty, string outDir, string? file,
                                     Action<bool, string> completed)
    {
        var session = new ZmodemSession(role, pty.Write, outDir, file);
        session.Completed += (ok, text) => completed(ok, text);
        var lastPercent = -10;
        session.Changed += z =>
        {
            if (z.Total <= 0) return;
            var pct = (int)(z.Bytes * 100 / z.Total);
            if (pct / 10 == lastPercent / 10) return;
            lastPercent = pct;
            Console.WriteLine($"  {z.CurrentName} {pct}% ({z.Bytes} of {z.Total})");
        };
        return session;
    }

    /// <summary>
    /// Everything after --command up to the next --flag, joined with spaces. A shell
    /// pipeline arrives as several argv entries when the launcher does not quote it.
    /// </summary>
    private static string? Command(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--command", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var parts = new List<string>();
        for (var i = index + 1; i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++)
            parts.Add(args[i]);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;
        return args[index + 1];
    }
}
