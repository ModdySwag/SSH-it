using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;
using SshIt.Views;
using Microsoft.Win32;

namespace SshIt;

public partial class App : Application
{
    private const string AppId = "SshIt.Desktop.1";

    /// <summary>How many extra windows a relaunch will restore. A runaway list is not a feature.</summary>
    private const int MaxExtraWindows = 6;

    private static Mutex? _mutex;
    private bool _ownsMutex;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;

    private readonly List<MainWindow> _windows = new();

    /// <summary>Every top-level window that owns a live terminal: main windows and tear-offs.</summary>
    private readonly List<Window> _surfaces = new();

    private bool _shuttingDown;
    private bool _snapshotTaken;

    public static App Instance => (App)Current;

    public AppSettings Settings { get; private set; } = new();
    public DataStore Store { get; private set; } = null!;
    public ThemeService Theme { get; private set; } = null!;
    public bool TrayAvailable => _tray is not null;

    /// <summary>Open main windows, oldest first. Tear-off windows are not in this list.</summary>
    public IReadOnlyList<MainWindow> OpenWindows => _windows;

    /// <summary>Every live pane in every surface, main windows and tear-offs alike.</summary>
    internal IEnumerable<TerminalPane> AllPanes()
    {
        foreach (var surface in _surfaces.ToList())
        {
            if (surface is MainWindow main)
                foreach (var pane in main.AllPanes()) yield return pane;
            else if (surface is Views.DetachedPaneWindow detached)
                foreach (var pane in detached.Panes()) yield return pane;
            else if (surface is Views.TmuxWindow tmux)
                foreach (var pane in tmux.Panes()) yield return pane;
        }
    }

    /// <summary>The window that was activated last; the target for tray clicks and handoffs.</summary>
    public MainWindow? MostRecentWindow { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PerfMetrics.MarkProcessStart();

        // ssh.exe runs SSH-it.exe as its askpass helper for saved passwords.
        // Answer on stdout and leave before any window is created.
        if (TryRunAsAskPass()) return;

        // Headless regression run for the terminal core.
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            var failures = SelfTest.Run();
            try { Console.Out.Write(File.ReadAllText(Path.Combine(Log.Directory, "selftest.txt"))); } catch { }
            Shutdown(failures);
            return;
        }


        // Agent surface: one-shot exec / sftp / stdio MCP. No window, no dispatcher.
        if (AgentCli.Wants(e.Args))
        {
            var code = AgentCli.Run(e.Args);
            Shutdown(code);
            return;
        }

        // ZMODEM regression rig: a real pty, a real transfer, no window.
        if (ZmodemProbe.Wants(e.Args))
        {
            var code = ZmodemProbe.Run(e.Args);
            Shutdown(code);
            return;
        }

        // tmux control-mode rig: a real server, a RAW pipe, no window and no ConPTY.
        if (TmuxProbe.Wants(e.Args))
        {
            var code = TmuxProbe.Run(e.Args);
            Shutdown(code);
            return;
        }

        // per-pane memory probe: real ConPTY panes, no window.
        if (MemoryProbe.Wants(e.Args))
        {
            var code = MemoryProbe.Run(e.Args);
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception);

        TrySetAppId();

        // Another instance already owns the app: hand it this launch's request and leave.
        // This is what makes a jump-list click or a shortcut work while the app is running.
        if (!SingleInstanceCheck())
        {
            Handoff.Send(Handoff.ForArgs(e.Args));
            Shutdown(0);
            return;
        }

        Store = new DataStore();
        Store.Load();
        Settings = Store.Settings;

        Theme = new ThemeService();
        // --hc pins the high-contrast dictionary for testing without touching the OS setting.
        if (e.Args.Contains("--hc", StringComparer.OrdinalIgnoreCase)) ThemeService.ForceHighContrast = true;
        Theme.Apply(Settings.Theme);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        CreateTray();

        // A clean start means one window and no restored tabs, so stale window entries go.
        if (!Settings.ReopenTabs && Settings.ExtraWindows.Count > 0)
        {
            Settings.ExtraWindows.Clear();
            Store.SaveSettings();
        }

        var pending = ArgValue(e.Args, "--connect");
        var first = CreateWindow(layout: null, tabs: Settings.OpenTabSessionIds, pendingSessionId: pending,
                                 restoreTabs: Settings.ReopenTabs, cascadeFrom: null, hooks: true);

        if (Settings.ReopenTabs)
        {
            foreach (var layout in Settings.ExtraWindows.Take(MaxExtraWindows).ToList())
                CreateWindow(layout, layout.TabSessionIds, pendingSessionId: null, restoreTabs: true,
                             cascadeFrom: null, hooks: false);
        }

        UpdateJumpList();

        // G2: the MCP server is a separate process, so it observes panes through this per-user pipe.
        // Started after the first window exists; a request that arrives before a pane is open gets a
        // clear "no open pane" answer rather than a dead pipe.
        AgentBridge.Start();

        // Jump-list tasks that open a dialog rather than a connection.
        if (e.Args.Contains("--new", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(first.OpenNewSessionDialog));
        else if (e.Args.Contains("--options", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(first.OpenOptionsDialog));
    }

    // ---------------------------------------------------------------- windows

    /// <summary>
    /// Opens a main window. A null layout with no cascade source is the primary window - the
    /// one the legacy placement fields describe. Every other window gets its own settings slot
    /// so it is restored next launch, and a window opened while the app is running steps
    /// down-right from the window it was opened from.
    /// </summary>
    public MainWindow CreateWindow(WindowLayout? layout = null, IEnumerable<string>? tabs = null,
                                   string? pendingSessionId = null, bool restoreTabs = false,
                                   MainWindow? cascadeFrom = null, bool hooks = false)
    {
        if (layout is null && cascadeFrom is not null)
        {
            // A new window opens at the size of the one it came from, then steps down-right.
            layout = new WindowLayout
            {
                Width = Math.Max(cascadeFrom.MinWidth, cascadeFrom.Width),
                Height = Math.Max(cascadeFrom.MinHeight, cascadeFrom.Height)
            };
            Settings.ExtraWindows.Add(layout);
        }

        var window = new MainWindow();
        window.ApplyLaunch(layout, tabs, pendingSessionId, restoreTabs, cascadeFrom, hooks);

        _windows.Add(window);
        _surfaces.Add(window);
        window.Activated += (_, _) => MostRecentWindow = window;
        window.Closed += (_, _) => OnWindowClosed(window);

        MostRecentWindow = window;
        MainWindow = window;

        window.Show();
        return window;
    }

    /// <summary>Registers a window that owns a live terminal but is not a main window (tear-off).</summary>
    public void TrackSurface(Window window)
    {
        if (!_surfaces.Contains(window)) _surfaces.Add(window);
        window.Closed += (_, _) =>
        {
            _surfaces.Remove(window);
            if (_surfaces.Count == 0 && !_shuttingDown) Quit();
        };
    }

    private void OnWindowClosed(MainWindow window)
    {
        _windows.Remove(window);
        _surfaces.Remove(window);
        if (MostRecentWindow == window) MostRecentWindow = _windows.LastOrDefault();

        if (!_snapshotTaken)
        {
            if (window.IsPrimary)
            {
                // The window that is left takes over the primary slot, so closing the first
                // window does not leave a phantom window behind in settings.
                var successor = _windows.FirstOrDefault();
                if (successor is not null)
                {
                    if (successor.Layout is { } successorSlot) Settings.ExtraWindows.Remove(successorSlot);
                    successor.BecomePrimary();
                }
            }
            else if (window.Layout is { } slot)
            {
                Settings.ExtraWindows.Remove(slot);
            }
        }

        if (_surfaces.Count == 0 && !_shuttingDown) Quit();
    }

    /// <summary>
    /// Freezes the set of open windows into settings before an app-wide exit, so the next
    /// launch restores exactly the windows that were on screen. Without this, closing them one
    /// by one would fold each extra window into the primary slot and lose the count.
    /// </summary>
    private void SnapshotWindows()
    {
        if (_windows.Count == 0) return;

        var primary = _windows.FirstOrDefault(w => w.IsPrimary) ?? _windows[0];
        primary.BecomePrimary();

        Settings.ExtraWindows.Clear();
        foreach (var window in _windows)
            if (!ReferenceEquals(window, primary) && window.Layout is { } slot)
                Settings.ExtraWindows.Add(slot);

        Store.SaveSettings();
    }

    /// <summary>Closes every window and exits. Used by File &gt; Exit and the tray menu.</summary>
    public void ExitAll()
    {
        SnapshotWindows();
        _snapshotTaken = true;

        foreach (var surface in _surfaces.ToList())
        {
            try
            {
                if (surface is MainWindow main) main.RequestClose();
                else surface.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("exit: " + ex.Message);
            }
        }

        if (_surfaces.Count == 0) Quit();
        else _snapshotTaken = false;   // a close was cancelled; windows are still open
    }

    private void Quit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { Store.SaveSettings(); } catch { }
        Shutdown(0);
    }

    /// <summary>Re-applies settings that every window shares after the Options dialog.</summary>
    public void ReloadWindowSettings()
    {
        foreach (var window in _windows) window.ReloadSettings();
    }

    // ---------------------------------------------------------------- handoff

    /// <summary>
    /// The first instance listens here. Each connection carries one request from a launch that
    /// found the mutex taken: focus, connect a session, open a dialog, or a new window.
    /// </summary>
    private void StartHandoffServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Handoff.PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();

                    string message;
                    // One request per connection, read before the client can disappear. The
                    // client does not wait for a reply, so there is nothing to write back.
                    using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true))
                        message = reader.ReadLine() ?? "";

                    Dispatcher.BeginInvoke(() => HandleHandoff(message));
                }
                catch (Exception ex)
                {
                    Log.Warn("handoff: " + ex.Message);
                    Thread.Sleep(250);
                }
            }
        })
        { IsBackground = true, Name = "handoff" };

        thread.Start();
    }

    private void HandleHandoff(string message)
    {
        var (verb, argument) = Handoff.Parse(message);

        if (verb == Handoff.NewWindow)
        {
            CreateWindow(cascadeFrom: MostRecentWindow);
            return;
        }

        var target = verb == Handoff.Connect
            ? _windows.FirstOrDefault(w => w.HasSession(argument)) ?? MostRecentWindow
            : MostRecentWindow;

        target?.ShowFromTray();

        switch (verb)
        {
            case Handoff.Connect:
                if (target is null) CreateWindow(pendingSessionId: argument);
                else target.OpenSessionById(argument);
                break;

            case Handoff.NewSession:
                (target ?? CreateWindow()).OpenNewSessionDialog();
                break;

            case Handoff.Options:
                (target ?? CreateWindow()).OpenOptionsDialog();
                break;

            default:
                target?.ShowFromTray();
                break;
        }
    }

    // ---------------------------------------------------------------- high contrast

    private static readonly string[] HighContrastTriggers =
    {
        "HighContrast", "WindowColor", "WindowTextColor", "HighlightColor", "GrayTextColor"
    };

    /// <summary>
    /// Windows high contrast can be switched on, or re-coloured, while the app is running.
    /// Rebuild the brush dictionary and re-theme the panes so nothing keeps a stale palette.
    /// The colour names come through this event as plain strings: SystemParameters has no
    /// WindowColor member of its own (SystemColors owns those), so they cannot be nameof'd.
    /// </summary>
    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && !HighContrastTriggers.Contains(e.PropertyName)) return;

        Dispatcher.BeginInvoke(() =>
        {
            Theme.Apply(Settings.Theme);
            foreach (var window in _windows) window.RefreshHighContrast();
        });
    }

    // ---------------------------------------------------------------- askpass

    private static bool TryRunAsAskPass()
    {
        var id = Environment.GetEnvironmentVariable("CONDUIT_ASKPASS");
        if (string.IsNullOrEmpty(id)) return false;

        try
        {
            var secret = CredentialVault.ReadAskPass(id);
            if (secret is null) return false;

            var stream = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(secret + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn("askpass: " + ex.Message);
        }
        Environment.Exit(0);
        return true;
    }

    // ---------------------------------------------------------------- single instance

    private bool SingleInstanceCheck()
    {
        _mutex = new Mutex(true, @"Local\SSH-it-single-instance", out var created);
        _ownsMutex = created;
        if (!created) return false;

        StartHandoffServer();
        return true;
    }

    // ---------------------------------------------------------------- command line

    private static string? ArgValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    // ---------------------------------------------------------------- jump list

    public void UpdateJumpList()
    {
        try
        {
            var list = new System.Windows.Shell.JumpList
            {
                ShowRecentCategory = false,
                ShowFrequentCategory = false
            };

            foreach (var id in Settings.RecentSessionIds.Take(8))
            {
                var session = Store.Sessions.FirstOrDefault(s => s.Id == id);
                if (session is null) continue;
                list.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = session.Name,
                    Description = session.DisplayTarget,
                    Arguments = "--connect " + session.Id,
                    CustomCategory = "Recent sessions"
                });
            }

            list.JumpItems.Add(new System.Windows.Shell.JumpTask
            {
                Title = "New window",
                Arguments = "--newwindow",
                CustomCategory = "Tasks"
            });
            list.JumpItems.Add(new System.Windows.Shell.JumpTask
            {
                Title = "New session",
                Arguments = "--new",
                CustomCategory = "Tasks"
            });
            list.JumpItems.Add(new System.Windows.Shell.JumpTask
            {
                Title = "Options",
                Arguments = "--options",
                CustomCategory = "Tasks"
            });

            System.Windows.Shell.JumpList.SetJumpList(Current, list);
            list.Apply();
        }
        catch (Exception ex)
        {
            Log.Warn("jump list: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- tray

    private void CreateTray()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ssh-it.ico");
            _trayIcon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Shield;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open SSH-it", null, (_, _) => MostRecentWindow?.ShowFromTray());
            menu.Items.Add("New window", null, (_, _) => CreateWindow(cascadeFrom: MostRecentWindow));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitAll());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = "SSH-it",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => MostRecentWindow?.ShowFromTray();
        }
        catch (Exception ex)
        {
            Log.Warn("tray: " + ex.Message);
            _tray = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            TunnelService.StopAll();
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (_ownsMutex) _mutex?.ReleaseMutex();
        }
        catch { }
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- errors

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception);
        MessageBox.Show(
            "SSH-it hit an unexpected error.\n\n" + e.Exception.Message +
            "\n\nDetails were written to:\n" + Log.FilePath,
            "SSH-it", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);


    private static void TrySetAppId()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { }
    }
}
