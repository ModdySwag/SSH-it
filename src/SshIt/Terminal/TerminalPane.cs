using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Terminal;

public enum SessionState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>
/// One terminal tab: ConPTY child + parser + view + the local chrome around it
/// (find bar, reconnect banner, context menu).
/// </summary>
public sealed class TerminalPane : Grid, IDisposable
{
    private readonly AppSettings _settings;
    private readonly TerminalView _view;
    private readonly TerminalBuffer _buffer;
    private readonly VtParser _parser;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findCount;
    private readonly Border _banner;
    private readonly TextBlock _bannerTitle;
    private readonly TextBlock _bannerText;
    private readonly Button _reconnectButton;
    private readonly Button _whyButton;
    private readonly Border _idlePanel;
    private readonly TextBlock _idleText;
    private readonly Button _idleConnect;

    private readonly object _incomingGate = new();
    private readonly List<byte[]> _incoming = new();
    private bool _flushScheduled;

    private ConPty? _pty;
    private ITerminalTransport? _transport;
    private TmuxLink? _tmux;
    private TmuxPaneBridge? _tmuxBridge;
    private bool _awaitingCapture;
    private string? _captureEnd;
    private bool _disposed;

    /// <summary>
    /// Bumped every time the current connection is retired. An Exited callback is posted to the
    /// dispatcher, so one from the previous pty can run AFTER a reconnect has started; without this
    /// guard it marked the fresh connection failed and scheduled a retry that killed it.
    /// </summary>
    private int _generation;

    /// <summary>The delegate currently subscribed to the pty/transport, so it can be unsubscribed exactly.</summary>
    private Action<int>? _exitHandler;
    private bool _locked;
    private byte[]? _lockSalt;
    private byte[]? _lockHash;
    private Border? _lockOverlay;
    private DateTime _connectedAt;
    private int _diagnosticBytes;
    private StreamWriter? _recordWriter;
    private DateTime _recordStart;
    private int _recordChunks;
    private string? _recordPath;
    private int _recordBlock;
    private bool _sawFirstOutput;
    private string? _diagnoseContext;
    private ZmodemSession? _zmodem;
    private int _zmodemPercent = -1;

    /// <summary>Short live text for the window status bar (a running ZMODEM transfer).</summary>
    public string? Activity { get; private set; }

    public Session Session { get; }
    public TerminalView View => _view;
    public TerminalBuffer Buffer => _buffer;
    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>No child has ever started in this pane — a restored tab waiting to be opened.</summary>
    public bool IsIdle => State == SessionState.Idle;
    public string? LastError { get; private set; }
    public int ConnectMilliseconds { get; private set; }

    /// <summary>The pty child's pid — 0 when this pane has no live pty.</summary>
    public int PtyProcessId => _pty is { ProcessId: > 0, IsRunning: true } ? _pty.ProcessId : 0;

    /// <summary>When that pty started (UTC); null when there is none.</summary>
    public DateTime? PtyStartedAt
        => _pty is { ProcessId: > 0, IsRunning: true } && _pty.StartedAtUtc != default ? _pty.StartedAtUtc : null;

    /// <summary>The last completed block's command, for pre-filling a watch or a diff.</summary>
    public string? LastCommand
    {
        get
        {
            var marks = _view.Buffer.Prompts;
            return marks.Count == 0 ? null : marks[^1].Command;
        }
    }

    /// <summary>A locked pane refuses typing, paste, broadcast input and closing until unlocked.</summary>
    public bool IsLocked => _locked;

    public event Action<TerminalPane>? StateChanged;
    public event Action<TerminalPane, string>? TitleChanged;
    public event Action<TerminalPane, string>? Notice;

    /// <summary>
    /// A failure banner asked "why". The payload is the evidence the pane already holds: ssh's
    /// error line plus the tail of the scrollback, so Diagnose can explain a failure that has
    /// since stopped happening.
    /// </summary>
    public event Action<TerminalPane, string>? DiagnoseRequested;

    /// <summary>R3-06: approve one command for the agent surface, bound to this pane's pty.</summary>
    public event Action<TerminalPane>? ApproveRequested;

    /// <summary>The shell's working directory as last reported by OSC 7 (null until it says).</summary>
    public event Action<TerminalPane, string>? WorkingDirectoryChanged;

    /// <summary>The directory the shell last reported, or null when it has not said.</summary>
    public string? WorkingDirectory => _buffer.WorkingDirectory;

    /// <summary>
    /// R3-24: a finished command block, for the window's accessibility live region. Separate from
    /// the evidence chain on purpose: the chain records, this speaks, and neither depends on the
    /// other being enabled.
    /// </summary>
    public event Action<PromptMark>? BlockCompleted;

    /// <summary>Raw bytes typed by the user (not shell output). Used for broadcast input.</summary>
    public event Action<byte[]>? UserInput;

    public TerminalPane(Session session, AppSettings settings)
        : this(session, settings, 110, 30)
    {
    }

    /// <summary>
    /// A pane attached to ONE tmux pane: the control stream replaces the pty, and the buffer is
    /// sized from tmux's layout so the pane's own wrapping matches what the server believes.
    /// The link is owned by the window and shared by its panes.
    /// </summary>
    public TerminalPane(Session session, AppSettings settings, TmuxLink link, int paneId, int columns, int rows)
        : this(session, settings, Math.Max(2, columns), Math.Max(1, rows))
    {
        AttachTmux(link, paneId);
    }

    private TerminalPane(Session session, AppSettings settings, int columns, int rows)
    {
        Session = session;
        _settings = settings;

        _buffer = new TerminalBuffer(columns, rows) { ScrollbackLimit = settings.ScrollbackLines };
        _parser = new VtParser(_buffer, bytes => WriteToChild(bytes));
        _view = new TerminalView();
        _view.AttachBuffer(_buffer);
        _view.ApplyTheme(TerminalTheme.Get(settings.TerminalTheme));
        _view.ApplyFont(settings.FontFamily, settings.FontSize);
        _view.ApplyHighlights(settings.HighlightRules);
        _view.Input += EmitInput;
        _view.GridSizeChanged += (cols, rows) =>
        {
            _pty?.Resize((short)cols, (short)rows);
            _transport?.Resize(cols, rows);
        };
        _view.LinkActivated += OpenLink;
        _view.TitleChanged += title => TitleChanged?.Invoke(this, title);
        _buffer.WorkingDirectoryChanged += path => WorkingDirectoryChanged?.Invoke(this, path);

        Children.Add(_view);

        // ---- idle card. A restored tab is opened with connect:false, so there is no pty and no
        // banner; without this the pane is a blank rectangle and the status line's "press Connect"
        // had nothing to press. Enter in the pane does the same thing (OnPreviewKeyDown).
        var idleTitle = new TextBlock
        {
            Text = "Not connected",
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("Brush.Text")
        };
        AutomationProperties.SetName(idleTitle, "Not connected");

        _idleText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 12),
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };

        _idleConnect = new Button
        {
            Content = "Connect",
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            Padding = new Thickness(18, 0, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAutomationId(_idleConnect, "IdleConnect");
        AutomationProperties.SetName(_idleConnect, "Connect " + session.Name);
        _idleConnect.Click += (_, _) => Connect(null);

        var idleStack = new StackPanel();
        idleStack.Children.Add(idleTitle);
        idleStack.Children.Add(_idleText);
        idleStack.Children.Add(_idleConnect);

        _idlePanel = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.Panel"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24, 18, 24, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = idleStack
        };
        Children.Add(_idlePanel);
        UpdateIdleCard();

        // ---- find bar
        _findBox = new TextBox
        {
            Width = 240,
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("Text.Input")
        };
        _findBox.TextChanged += (_, _) => RunSearch();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                _view.FindNext((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                UpdateFindCount();
                e.Handled = true;
            }
        };

        _findCount = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };

        var findPrev = MakeIconButton("\uE70E", "Previous match", () => { _view.FindNext(true); UpdateFindCount(); });
        var findNext = MakeIconButton("\uE70D", "Next match", () => { _view.FindNext(false); UpdateFindCount(); });
        var findClose = MakeIconButton("\uE711", "Close find", HideFind);

        var findStack = new StackPanel { Orientation = Orientation.Horizontal };
        findStack.Children.Add(_findBox);
        findStack.Children.Add(_findCount);
        findStack.Children.Add(findPrev);
        findStack.Children.Add(findNext);
        findStack.Children.Add(findClose);

        _findBar = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.Elevated"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 8, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = findStack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.35
            }
        };
        Children.Add(_findBar);

        // ---- reconnect banner
        _bannerTitle = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Brush.Text")
        };
        _bannerText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        _reconnectButton = new Button
        {
            Content = "Reconnect",
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 0, 16, 0)
        };
        _reconnectButton.Click += (_, _) => Connect(null);

        var bannerStack = new StackPanel();
        bannerStack.Children.Add(_bannerTitle);
        bannerStack.Children.Add(_bannerText);

        _whyButton = new Button
        {
            Content = "Why?",
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Explain this failure: the effective configuration, a live handshake, " +
                      "and what the diagnostic text of this pane points to."
        };
        _whyButton.Click += (_, _) =>
        {
            if (_diagnoseContext is { Length: > 0 } context) DiagnoseRequested?.Invoke(this, context);
        };

        var bannerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        bannerButtons.Children.Add(_reconnectButton);
        bannerButtons.Children.Add(_whyButton);
        bannerStack.Children.Add(bannerButtons);

        _banner = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.Panel"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = bannerStack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 30,
                ShadowDepth = 4,
                Opacity = 0.45
            }
        };
        Children.Add(_banner);

        BuildContextMenu();

        PreviewKeyDown += OnPreviewKeyDown;
        _buffer.TitleChanged += _ => { };

        // Evidence chain (R3-04): one signed row per completed command block. Never throws into
        // the pane; if the chain cannot be written the terminal carries on.
        _buffer.BlockFinished += mark =>
        {
            try
            {
                var output = _buffer.BlockOutput(mark);
                CommandLedger.Append(Session, mark.Command, mark.ExitCode, mark.Duration, mark.Cwd, output);
            }
            catch { /* the ledger never breaks the terminal */ }

            try { BlockCompleted?.Invoke(mark); } catch { }
        };
    }

    private Button MakeIconButton(string glyph, string tooltip, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
                FontSize = 11
            },
            Style = (Style)Application.Current.FindResource("Btn.Icon"),
            Width = 28,
            Height = 28,
            ToolTip = tooltip
        };
        button.Click += (_, _) => click();
        return button;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Copy", "Ctrl+Shift+C", CopySelection));
        menu.Items.Add(Item("Paste", "Ctrl+Shift+V", PasteClipboard));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy last command", "Ctrl+Shift+U", CopyLastCommand));
        menu.Items.Add(Item("Copy last output", "Ctrl+Shift+O", CopyLastOutput));
        menu.Items.Add(Item("Copy last command and output", "", CopyLastBlock));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save receipt…", "Ctrl+Shift+R", SaveReceipt));
        menu.Items.Add(Item("Copy receipt as Markdown", "", CopyReceiptMarkdown));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Jump to previous prompt", "Ctrl+Shift+Up", () => _view.JumpToPrompt(-1)));
        menu.Items.Add(Item("Jump to next prompt", "Ctrl+Shift+Down", () => _view.JumpToPrompt(1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select all", "Ctrl+Shift+A", () => _view.SelectAll()));
        menu.Items.Add(Item("Find…", "Ctrl+F", ShowFind));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Clear screen", "", () => WriteToChild("\u000c")));
        menu.Items.Add(Item("Reset terminal", "", ResetTerminal));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reconnect", "", () => Connect(null)));
        menu.Items.Add(Item("Lock session…", "", OpenLockDialog));
        menu.Items.Add(Item("Approve a command for agents…", "", () => ApproveRequested?.Invoke(this)));
        menu.Items.Add(Item("Close", "Ctrl+Shift+Q", () => Notice?.Invoke(this, "close")));

        ContextMenu = menu;
    }


    // ---------------------------------------------------------------- session lock

    /// <summary>Asks for a password and locks this pane; when already locked it asks for the password.</summary>
    public void OpenLockDialog()
    {
        if (_locked) { PromptUnlock(); return; }
        var dialog = new Views.LockSessionWindow(Session.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        LockSession(dialog.Password, dialog.HideOutput);
    }

    public void LockSession(string password, bool hideOutput)
    {
        _lockSalt = SessionLock.NewSalt();
        _lockHash = SessionLock.Hash(password, _lockSalt);
        _locked = true;
        BuildLockOverlay(hideOutput);
        _lockOverlay?.Focus();
        Notice?.Invoke(this, "locked");
    }

    public bool TryUnlock(string password)
    {
        if (!_locked) return true;
        if (!SessionLock.Verify(password, _lockSalt, _lockHash)) return false;

        _locked = false;
        _lockSalt = null;
        _lockHash = null;
        if (_lockOverlay is not null)
        {
            Children.Remove(_lockOverlay);
            _lockOverlay = null;
        }
        _view.Focus();
        Notice?.Invoke(this, "unlocked");
        return true;
    }

    public void PromptUnlock()
    {
        if (!_locked) return;
        var dialog = new Views.UnlockSessionWindow(Session.Name, TryUnlock) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void BuildLockOverlay(bool hideOutput)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Session locked",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("Brush.Text")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Typing, pasting and closing are blocked. The session keeps running and keeps its scrollback.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 14),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        });
        var unlock = new Button
        {
            Content = "Unlock…",
            Height = 32,
            MinWidth = 130,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.FindResource("Btn.Accent")
        };
        unlock.Click += (_, _) => PromptUnlock();
        panel.Children.Add(unlock);

        // An opaque brush hides the output; a scrim keeps it readable but unusable.
        var overlay = new Border
        {
            Background = hideOutput
                ? (Brush)Application.Current.FindResource("Brush.Window")
                : new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            Child = panel,
            Focusable = true
        };

        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Unlock…" };
        item.Click += (_, _) => PromptUnlock();
        menu.Items.Add(item);
        overlay.ContextMenu = menu;

        _lockOverlay = overlay;
        Children.Add(overlay);
    }

    // ---------------------------------------------------------------- command blocks

    private PromptMark? LastBlock => _view.Buffer.Prompts.Count > 0 ? _view.Buffer.Prompts[^1] : null;

    /// <summary>Copies the exact command when the shell sent one, else the line it was typed on.</summary>
    public void CopyLastCommand()
    {
        if (LastBlock?.Command is { Length: > 0 } command) Clipboard.SetText(command);
    }

    public void CopyLastOutput()
    {
        var block = LastBlock;
        if (block is null) return;

        var text = _view.Buffer.BlockOutput(block);
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    public void CopyLastBlock()
    {
        var block = LastBlock;
        if (block is null) return;

        var parts = new List<string>();
        if (block.Command is { Length: > 0 } command) parts.Add(command);
        var output = _view.Buffer.BlockOutput(block);
        if (!string.IsNullOrEmpty(output)) parts.Add(output);
        if (parts.Count > 0) Clipboard.SetText(string.Join("\n", parts));
    }

    /// <summary>Builds a receipt for the newest block; null when the block never opened.</summary>
    public BlockReceipt.Receipt? BuildReceipt()
    {
        var block = LastBlock;
        if (block is null) return null;

        var output = _view.Buffer.BlockOutput(block);

        return new BlockReceipt.Receipt
        {
            Host = string.IsNullOrWhiteSpace(Session.Host) ? Session.Name : Session.Host,
            SessionName = Session.Name,
            User = string.IsNullOrWhiteSpace(Session.User) ? null : Session.User,
            Command = block.Command,
            Output = output ?? "",
            Cwd = string.IsNullOrEmpty(block.Cwd) ? WorkingDirectory : block.Cwd,
            ExitCode = block.ExitCode,
            Duration = block.Duration,
            Started = block.Started,
            AppVersion = BlockReceipt.AppVersion()
        };
    }

    /// <summary>Writes the newest block as a standalone HTML card, or Markdown by extension.</summary>
    public void SaveReceipt()
    {
        var receipt = BuildReceipt();
        if (receipt is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save command receipt",
            Filter = "HTML receipt (*.html)|*.html|Markdown (*.md)|*.md|All files (*.*)|*.*",
            FileName = BlockReceipt.SuggestFileName(receipt),
            DefaultExt = ".html",
            AddExtension = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var markdown = Path.GetExtension(dialog.FileName).Equals(".md", StringComparison.OrdinalIgnoreCase);
        var text = markdown ? BlockReceipt.RenderMarkdown(receipt) : BlockReceipt.RenderHtml(receipt);
        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
    }

    public void CopyReceiptMarkdown()
    {
        var receipt = BuildReceipt();
        if (receipt is not null) Clipboard.SetText(BlockReceipt.RenderMarkdown(receipt));
    }

    // ---------------------------------------------------------------- lifecycle

    private DateTime _lastActivity = DateTime.MinValue;

    /// <summary>
    /// "This pane produced output" — throttled to four signals a second, because that is the most a
    /// tab marker can show and a chatty build would otherwise repaint the tab strip per packet.
    /// </summary>
    public event Action<TerminalPane>? OutputArrived;

    private void NoteOutput()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastActivity).TotalMilliseconds < 250) return;
        _lastActivity = now;
        try { OutputArrived?.Invoke(this); } catch { }
    }

    private System.Windows.Threading.DispatcherTimer? _retryTimer;
    private System.Windows.Threading.DispatcherTimer? _stableTimer;
    private int _retryAttempt;
    private bool _userStopped;

    /// <summary>Retry an unexpected drop on a short backoff ladder; a clean exit is never retried.</summary>
    public bool AutoReconnect { get; set; } = true;

    public void Connect(string? secret) => ConnectCore(secret, automatic: false);

    /// <summary>
    /// <paramref name="automatic"/> marks a retry the ladder scheduled itself. That distinction is the
    /// whole ladder: a manual Connect starts the backoff from the beginning, while an automatic retry
    /// must leave the counter alone or every attempt resets to the first delay and an unreachable host
    /// is retried every two seconds for as long as the window is open.
    /// </summary>
    private void ConnectCore(string? secret, bool automatic)
    {
        if (_disposed) return;
        DisposePty();
        _generation++;

        _userStopped = false;
        if (!automatic) _retryAttempt = 0;
        _retryTimer?.Stop();
        _retryTimer = null;
        _stableTimer?.Stop();
        _stableTimer = null;

        LastError = null;
        _sawFirstOutput = false;
        PerfMetrics.ResetKey();
        SetState(SessionState.Connecting);
        StartRecording();
        _buffer.AppendNotice($"Connecting to {Session.DisplayTarget}…");

        try
        {
            if (Session.IsLocal)
            {
                StartLocalShell();
                return;
            }

            if (Session.Kind == "Telnet") { StartTelnet(); return; }
            if (Session.Kind == "Serial") { StartSerial(); return; }
            if (string.Equals(Session.Kind, "Tmux", StringComparison.OrdinalIgnoreCase))
            {
                // A tmux pane is constructed already attached to a shared link by TmuxWindow.
                // Starting ssh here would give the user a plain shell behind a session that says
                // tmux, which is worse than saying so.
                LastError = "tmux sessions open through the tmux window (Tools → tmux control mode…).";
                SetState(SessionState.Failed);
                _buffer.AppendNotice(LastError);
                return;
            }

            var args = SshLauncher.BuildArguments(Session);
            Log.Info($"ssh {args}");
            var askPass = string.IsNullOrEmpty(secret) ? null : CredentialVault.StoreAskPass(Session.Id, secret!);
            var env = SshLauncher.BuildEnvironment(Session, askPass);

            _pty = new ConPty();
            _pty.DataReceived += OnData;
            _exitHandler = ExitHandler();
            _pty.Exited += _exitHandler;
            _connectedAt = DateTime.Now;
            _pty.Start(SshLauncher.SshExe, args, 110, 30, null, env);

            if (!string.IsNullOrWhiteSpace(Session.StartupCommand))
            {
                var command = Session.StartupCommand!;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (State == SessionState.Connected)        WriteToChild(command + "\r");
                };
                timer.Start();
            }

            // Deliberately no "the child is still alive, so call it connected" timer. On a dead link
            // ssh stays alive for the whole ConnectTimeout and then exits, so such a timer turned
            // 20 s of every retry into a green Connected pane that swallowed what you typed. SSH
            // earns Connected on its first output — a prompt, an MOTD, a host-key question and a
            // password prompt all arrive before anything a user could act on. Local, telnet and
            // serial panes never reach this code; they start their own transports above.

            // -X alone is not enough on Windows: without a reachable X server the first GUI
            // program on the host fails. Say so after the shell's own banner and clear-screen
            // have gone through, or the notice is wiped before anyone sees it.
            if (Session.X11Forwarding && !X11Support.Available)
            {
                var warn = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                warn.Tick += (_, _) =>
                {
                    warn.Stop();
                    if (_disposed) return;
                    _buffer.AppendNotice("X11 forwarding is on, but no X server was found on 127.0.0.1 " +
                                         "(displays :0 to :5). Start VcXsrv or X410, or set DISPLAY, then reconnect.");
                };
                warn.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "connect " + Session.Id);
            LastError = ex.Message;
            SetState(SessionState.Failed);
            ShowBanner("Could not start the SSH client", ex.Message, DiagnosisContext(ex.Message));
        }
    }

    private void WriteToChild(byte[] bytes)
    {
        // tmux control mode has no child on this side: keys are a command to the server.
        if (_tmuxBridge is not null)
        {
            if (_tmux is null || _locked) return;
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length > 0) _tmux.Send(_tmuxBridge.KeySequence(text));
            return;
        }

        if (_pty is not null) _pty.Write(bytes);
        else _transport?.Write(bytes);
    }

    private void WriteToChild(string text) => WriteToChild(Encoding.UTF8.GetBytes(text));

    /// <summary>Raw TCP terminal (telnet): no SSH, no SFTP, no tunnels — the UI says so per session.</summary>
    private void StartTelnet()
    {
        var transport = new TelnetTransport();
        transport.DataReceived += OnData;
        _exitHandler = ExitHandler();
        transport.Exited += _exitHandler;
        _transport = transport;
        _connectedAt = DateTime.Now;
        transport.Start(Session.Host, Session.Port <= 0 ? 23 : Session.Port, 110, 30);
    }

    /// <summary>Serial terminal: bytes on a COM port, straight into the same parser.</summary>
    private void StartSerial()
    {
        var transport = new SerialTransport();
        transport.DataReceived += OnData;
        _exitHandler = ExitHandler();
        transport.Exited += _exitHandler;
        _transport = transport;
        _connectedAt = DateTime.Now;
        transport.Start(Session.SerialPortName ?? "COM1", Session.SerialBaud, Session.SerialDataBits,
                        Session.SerialParity, Session.SerialStopBits, Session.SerialFlow);
    }

    private void StartLocalShell()
    {
        var shell = Session.LocalShell;
        if (string.IsNullOrWhiteSpace(shell))
        {
            var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            shell = File.Exists(pwsh)
                ? pwsh
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        _pty = new ConPty();
        _pty.DataReceived += OnData;
        _exitHandler = ExitHandler();
        _pty.Exited += _exitHandler;
        _connectedAt = DateTime.Now;
        _pty.Start(shell, File.Exists(shell) && shell.EndsWith("pwsh.exe") ? "-NoLogo" : "-NoLogo -NoExit", 110, 30);
    }


    // ---------------------------------------------------------------- recording (asciinema v2)

    private void StartRecording()
    {
        StopRecording();
        if (!_settings.RecordSessions) return;

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "SSH-it", "recordings");
            Directory.CreateDirectory(dir);

            var safe = new string(Session.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var path = Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.cast");

            _recordStart = DateTime.UtcNow;
            _recordChunks = 0;
            _recordPath = path;
            _recordBlock = 0;
            _recordWriter = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = false };

            var header = "{\"version\":2,\"width\":" + _buffer.Columns +
                         ",\"height\":" + _buffer.Rows +
                         ",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
                         ",\"env\":{\"TERM\":\"xterm-256color\",\"SHELL\":\"/bin/sh\"}}";
            _recordWriter.WriteLine(header);
            Log.Info("recording to " + path);
        }
        catch (Exception ex)
        {
            Log.Warn("recording: " + ex.Message);
            _recordWriter = null;
        }
    }

    private void RecordChunk(byte[] chunk)
    {
        if (_recordWriter is null) return;
        try
        {
            var seconds = (DateTime.UtcNow - _recordStart).TotalSeconds;
            var text = Encoding.UTF8.GetString(chunk);
            var escaped = System.Text.Json.JsonSerializer.Serialize(text);
            _recordWriter.WriteLine("[" + seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                                    ",\"o\"," + escaped + "]");
            if (++_recordChunks % 40 == 0) _recordWriter.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn("recording write: " + ex.Message);
            StopRecording();
        }
    }

    private void StopRecording()
    {
        var writer = _recordWriter;
        _recordWriter = null;
        _recordPath = null;
        if (writer is null) return;
        try { writer.Flush(); writer.Dispose(); } catch { }
    }

    private void OnData(byte[] data, int count)
    {
        PerfMetrics.Received(count);
        var copy = new byte[count];
        System.Buffer.BlockCopy(data, 0, copy, 0, count);
        lock (_incomingGate)
        {
            _incoming.Add(copy);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Dispatcher.BeginInvoke(FlushIncoming, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FlushIncoming()
    {
        List<byte[]> chunks;
        lock (_incomingGate)
        {
            chunks = new List<byte[]>(_incoming);
            _incoming.Clear();
            _flushScheduled = false;
        }

        // Nothing to feed: a flush queued by a connection that has since been retired. Without this
        // the tail below would mark the NEW connection Connected before its child printed anything.
        if (chunks.Count == 0) return;

        foreach (var chunk in chunks)
        {
            try
            {
                if (_diagnosticBytes < 4096)
                {
                    _diagnosticBytes += chunk.Length;
                    Log.Info("ssh out: " + Encoding.UTF8.GetString(chunk).Replace("\u001b", "ESC").Trim());
                }

                if (_zmodem is not null)
                {
                    _zmodem.Feed(chunk, 0, chunk.Length);
                }
                else
                {
                    var start = _settings.Zmodem ? ZmodemSession.FindStart(chunk, chunk.Length) : -1;
                    if (start < 0)
                    {
                        _parser.Feed(chunk);
                        NoteOutput();
                    }
                    else
                    {
                        // Text before the header still belongs to the screen; the rest is protocol.
                        if (start > 0)
                        {
                            var prefix = new byte[start];
                            System.Buffer.BlockCopy(chunk, 0, prefix, 0, start);
                            _parser.Feed(prefix);
                        }
                        StartZmodem(ZmodemRole.Receive, null);
                        _zmodem?.Feed(chunk, start, chunk.Length - start);
                    }
                }
                RecordChunk(chunk);
                PerfMetrics.Parsed(chunk.Length);
            }
            catch (Exception ex) { Log.Error(ex, "parser"); }
        }

        // The proof only counts while the child is alive. A connection that has already died can
        // still have its last words sitting in this queue, and marking THOSE as connected put a
        // dead host back to green for the whole retry delay.
        if (!_sawFirstOutput && _pty is { IsRunning: true } && chunks.Any(c => ProvesConnection(c)))
        {
            _sawFirstOutput = true;
            ConnectMilliseconds = (int)(DateTime.Now - _connectedAt).TotalMilliseconds;
            MarkConnected();
        }
        _view.RequestRepaint();
    }

    /// <summary>
    /// True when a chunk carries something the FAR end produced. Windows ConPTY writes a short
    /// prologue of private-mode sequences the moment a pty starts — <c>ESC[?9001h</c> for win32
    /// input, <c>ESC[?1004h</c> for focus reporting — before the child has said a word. Counting
    /// that as output marked every attempt Connected, including one that was about to time out,
    /// which is exactly the lie this pane exists not to tell. A chunk made only of mode set/reset
    /// sequences is ConPTY talking; anything else — text, a prompt, an OSC title, a host-key or
    /// password question — is the connection answering.
    /// </summary>
    internal static bool ProvesConnection(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0) return false;
        var i = 0;
        var sawMode = false;
        while (i < chunk.Length)
        {
            if (chunk[i] != 0x1b || i + 2 >= chunk.Length || chunk[i + 1] != (byte)'[' || chunk[i + 2] != (byte)'?')
                return true;
            var j = i + 3;
            while (j < chunk.Length && chunk[j] >= (byte)'0' && chunk[j] <= (byte)'9') j++;
            if (j >= chunk.Length) return false;                  // half a prologue: wait for the rest
            if (chunk[j] != (byte)'h' && chunk[j] != (byte)'l') return true;
            sawMode = true;
            i = j + 1;
        }
        return !sawMode;
    }

    private void MarkConnected()
    {
        if (State == SessionState.Connected) return;
        SetState(SessionState.Connected);
        _view.SetConnected(true);
        _banner.Visibility = Visibility.Collapsed;
        _idlePanel.Visibility = Visibility.Collapsed;
        ConnectMilliseconds = ConnectMilliseconds == 0
            ? (int)(DateTime.Now - _connectedAt).TotalMilliseconds
            : ConnectMilliseconds;
        Session.LastUsed = DateTime.Now;
        Session.UseCount++;
        StartStabilityTimer();
    }

    /// <summary>
    /// A connection only counts as holding after ten seconds of being Connected. ssh can print a
    /// prompt and still die a second later (a host that accepts and then drops), and restarting the
    /// ladder on that would retry a broken host every two seconds forever.
    /// </summary>
    private void StartStabilityTimer()
    {
        _stableTimer?.Stop();
        _stableTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _stableTimer.Tick += (_, _) =>
        {
            _stableTimer?.Stop();
            _stableTimer = null;
            if (State == SessionState.Connected) _retryAttempt = 0;
        };
        _stableTimer.Start();
    }

    private Action<int> ExitHandler()
    {
        var generation = _generation;
        return exitCode => OnExited(exitCode, generation);
    }



    private void OnExited(int exitCode, int generation)
    {
        Log.Warn($"pty exited session={Session.Id} code=0x{exitCode:X8} ({exitCode})");
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || generation != _generation) return;   // a retired connection's goodbye
            _stableTimer?.Stop();
            _stableTimer = null;
            _view.SetConnected(false);
            SetState(exitCode == 0 ? SessionState.Disconnected : SessionState.Failed);

            // A shell that exits cleanly is the user's business; a non-zero exit is a drop worth retrying.
            if (exitCode != 0 && !_userStopped) { ScheduleReconnect(); return; }

            if (_settings.ReopenTabs && exitCode != 0)
            {
                var message = $"The SSH client exited with code {exitCode}.\n" +
                              DescribeExit(exitCode) + "\n\nScrollback is kept — reconnect when you are ready.";
                ShowBanner("Connection closed", message, DiagnosisContext(message));
            }
            else
            {
                var message = exitCode == 0
                    ? "The connection was closed."
                    : $"The SSH client exited with code {exitCode}.";
                ShowBanner("Session ended", message, exitCode == 0 ? null : DiagnosisContext(message));
            }
        });
    }

    private static string DescribeExit(int code) => code switch
    {
        255 => "255 usually means the SSH client could not connect (network, DNS, authentication or host key).",
        127 => "127 means the remote command was not found.",
        1 => "1 is a generic remote session failure.",
        _ => "See the scrollback above for the reason."
    };

    /// <summary>
    /// Schedules the next attempt. The banner carries the countdown, so a silently waiting pane can
    /// never look like a hung one; Disconnect stops the ladder and a manual Connect resets it.
    /// </summary>
    private void ScheduleReconnect()
    {
        var delay = ReconnectPolicy.NextDelay(_retryAttempt, _userStopped, AutoReconnect);
        if (delay is null)
        {
            ShowBanner("Not reconnecting",
                "The connection ended and the retry ladder is done. Press Connect to try again.",
                DiagnosisContext("The SSH client exited."));
            return;
        }

        _retryAttempt++;
        var seconds = delay.Value;
        ShowBanner("Connection lost", ReconnectPolicy.Message(seconds),
            DiagnosisContext("The SSH client exited."));

        _retryTimer?.Stop();
        _retryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer?.Stop();
            _retryTimer = null;
            if (_userStopped || _disposed) return;
            ConnectCore(CredentialVault.GetSessionSecret(Session.Id), automatic: true);
        };
        _retryTimer.Start();
    }

    private void SetState(SessionState state)
    {
        State = state;
        Session.IsOnline = state == SessionState.Connected;
        UpdateIdleCard();
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// The "Not connected" card belongs to a pane that has never started a child — that is, a tab
    /// restored lazily by ApplyLaunch. A pane that connected and then dropped keeps its banner
    /// instead, because "reconnect" and "open" are different promises.
    /// </summary>
    private void UpdateIdleCard()
    {
        var idle = State == SessionState.Idle;
        _idlePanel.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        if (!idle) return;
        _idleText.Text = "Restored tab — nothing has been started yet.\n" +
                         $"Press Enter or click Connect to open {Session.DisplayTarget}.";
    }

    private void ShowBanner(string title, string text, string? diagnoseContext = null)
    {
        _bannerTitle.Text = title;
        _bannerText.Text = text;
        _diagnoseContext = diagnoseContext;
        _whyButton.Visibility = string.IsNullOrWhiteSpace(diagnoseContext)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _banner.Visibility = Visibility.Visible;
        _idlePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>The banner's own words plus the scrollback tail: what ssh said before it died.</summary>
    private string DiagnosisContext(string message) =>
        message + "\n\nRecent output:\n" + _buffer.Tail(24);

    public void Disconnect()
    {
        _userStopped = true;
        _generation++;   // a pending exit callback belongs to the connection being retired
        _retryTimer?.Stop();
        _retryTimer = null;
        _stableTimer?.Stop();
        _stableTimer = null;

        if (_tmuxBridge is not null)
        {
            // The window owns the link: one pane cannot detach the whole client, and pretending
            // otherwise would leave the other panes drawing a stream nobody reads.
            _view.SetConnected(false);
            SetState(SessionState.Disconnected);
            _buffer.AppendNotice("Detached. Close the tmux window to end the connection.");
            return;
        }

        DisposePty();
        _view.SetConnected(false);
        SetState(SessionState.Disconnected);
        _buffer.AppendNotice("Disconnected.");
    }

    private void DisposePty()
    {
        var pty = _pty;
        _pty = null;

        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            try
            {
                transport.DataReceived -= OnData;
                if (_exitHandler is not null) transport.Exited -= _exitHandler;
                transport.Dispose();
            }
            catch (Exception ex) { Log.Error(ex, "transport dispose"); }
        }

        // A flush already queued at Input priority belongs to the connection being retired; letting it
        // run would feed the old child's bytes into the fresh parser and can mark the new one Connected.
        lock (_incomingGate)
        {
            _incoming.Clear();
            _flushScheduled = false;
        }

        if (pty is null) { _exitHandler = null; return; }
        Log.Warn($"DisposePty session={Session.Id} pid={pty.ProcessId}{Environment.NewLine}{Environment.StackTrace}");
        try
        {
            pty.DataReceived -= OnData;
            if (_exitHandler is not null) pty.Exited -= _exitHandler;
            _exitHandler = null;
            pty.Dispose();
        }
        catch (Exception ex) { Log.Error(ex, "pty dispose"); }
    }

    private void ResetTerminal()
    {
        _buffer.FullReset();
        _view.ScrollToBottom();
    }

    // ---------------------------------------------------------------- tmux control mode

    /// <summary>Non-null when this pane draws one tmux pane instead of a pty.</summary>
    public TmuxPaneBridge? TmuxBridge => _tmuxBridge;

    /// <summary>The tmux pane id (`%N`), or -1 when this is a normal terminal.</summary>
    public int TmuxPaneId => _tmuxBridge?.PaneId ?? -1;

    private void AttachTmux(TmuxLink link, int paneId)
    {
        _tmux = link;
        _tmuxBridge = new TmuxPaneBridge(_buffer, paneId);
        link.Event += OnTmuxEvent;
        _buffer.AppendNotice($"tmux pane %{paneId}");
        _view.SetConnected(true);
        _banner.Visibility = Visibility.Collapsed;
        _idlePanel.Visibility = Visibility.Collapsed;
        _connectedAt = DateTime.Now;
        SetState(SessionState.Connected);
    }

    private void OnTmuxEvent(TmuxEvent produced)
    {
        if (_disposed || _tmuxBridge is null) return;

        switch (produced.Kind)
        {
            case TmuxEventKind.Output when produced.PaneId == _tmuxBridge.PaneId || produced.PaneId < 0:
                _tmuxBridge.Apply(produced);
                _view.RequestRepaint();
                break;

            case TmuxEventKind.Pause when produced.PaneId == _tmuxBridge.PaneId || produced.PaneId < 0:
                RequestTmuxResync();
                break;

            case TmuxEventKind.End:
                // The marker is unique per pane and is sent straight after the capture, so the
                // END immediately before it is the capture even when typing produced other replies.
                if (_awaitingCapture && produced.Text.Contains("SSHIT-RESYNC-" + _tmuxBridge.PaneId + "-END", StringComparison.Ordinal))
                {
                    _awaitingCapture = false;
                    _tmuxBridge.ApplyCapture(_captureEnd ?? "");
                    _captureEnd = null;
                    _tmux?.Send(TmuxPaneBridge.ResumeSequence(_tmuxBridge.PaneId));
                    _view.ScrollToBottom();
                    _view.RequestRepaint();
                }
                else if (_awaitingCapture) _captureEnd = produced.Text;
                break;

            case TmuxEventKind.Exit:
                _view.SetConnected(false);
                SetState(SessionState.Disconnected);
                _buffer.AppendNotice("tmux detached"
                                     + (string.IsNullOrWhiteSpace(produced.Text) ? "." : ": " + produced.Text.Trim()));
                break;
        }
    }

    /// <summary>
    /// A paused pane has a stale screen: ask for `capture-pane -p -e`, then the marker; the reply
    /// replaces the visible screen and resumes the server. Safe to call for the initial paint too.
    /// </summary>
    public void RequestTmuxResync()
    {
        if (_tmux is null || _tmuxBridge is null) return;
        _awaitingCapture = true;
        _captureEnd = null;
        _tmux.Send(_tmuxBridge.ResyncSequence());
        _tmux.Send(_tmuxBridge.ResyncMarker());
    }

    // ---------------------------------------------------------------- input helpers

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // A restored tab has no child process, so Enter would otherwise be swallowed by the view
        // and the tab stayed closed while the window told the user to press it.
        if (!ctrl && !shift && e.Key == Key.Enter && State == SessionState.Idle)
        {
            Connect(null);
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { PasteClipboard(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.A) { _view.SelectAll(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Up) { _view.JumpToPrompt(-1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Down) { _view.JumpToPrompt(1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.U) { CopyLastCommand(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.O) { CopyLastOutput(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.R) { SaveReceipt(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { ShowFind(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Insert) { CopySelection(); e.Handled = true; return; }
        if (shift && e.Key == Key.Insert) { PasteClipboard(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.W) { Notice?.Invoke(this, "close"); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C && _view.HasSelection) { CopySelection(); e.Handled = true; return; }
    }

    public void CopySelection()
    {
        if (!_view.HasSelection) return;
        try { Clipboard.SetText(_view.GetSelectionText()); }
        catch (Exception ex) { Log.Warn("clipboard copy: " + ex.Message); }
    }

    public void PasteClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (_settings.WarnOnMultiPaste && text.Contains('\n'))
            {
                var lines = text.Split('\n').Length;
                if (lines > 3)
                {
                    var answer = MessageBox.Show(
                        $"Paste {lines} lines into the terminal?\n\n" +
                        "Multi-line paste is a common way to run unintended commands.",
                        "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.OK) return;
                }
            }
            _view.Paste(text);
        }
        catch (Exception ex) { Log.Warn("clipboard paste: " + ex.Message); }
    }

    public void SendCommand(string command) => SendInput(command, submit: true);

    /// <summary>
    /// Writes text to the pty. <paramref name="submit"/> appends the carriage return the shell
    /// needs; leave it false to put a half-typed template on the prompt for the user to finish.
    /// </summary>
    public void SendInput(string command, bool submit)
    {
        if (string.IsNullOrEmpty(command) && !submit) return;
        var text = (command ?? "").Replace("\r\n", "\r");
        EmitInput(Encoding.UTF8.GetBytes(submit ? text + "\r" : text));
        _view.ScrollToBottom();
        _view.Focus();
    }

    /// <summary>Writes to the pty without re-raising UserInput (used for broadcast and fan-out).</summary>
    /// <summary>False when the pane refused (locked) or has no live child: a fan-out counts deliveries, not attempts.</summary>
    public bool SendRaw(byte[] bytes)
    {
        if (_locked) return false;
        if (_tmuxBridge is not null)
        {
            if (_tmux is null || !_tmux.Running) return false;
            _tmux.Send(_tmuxBridge.KeySequence(Encoding.UTF8.GetString(bytes)));
            return true;
        }
        // `_pty` stays non-null after a drop (only DisposePty clears it), so nullness is not liveness:
        // a fan-out into a dead pane used to be counted as delivered.
        if (_pty is { IsRunning: true }) { _pty.Write(bytes); return true; }
        if (_transport is { IsRunning: true }) { _transport.Write(bytes); return true; }
        return false;
    }

    private void EmitInput(byte[] bytes)
    {
        if (_locked) return;
        // Only a visible, connected pane paints an echo, and the measurement ends at that paint:
        // keys typed into an idle or hidden tab would otherwise be attributed to a much later frame.
        if (IsVisible && State == SessionState.Connected) PerfMetrics.Key();
        WriteToChild(bytes);
        UserInput?.Invoke(bytes);
    }

    public void SendText(string text) => WriteToChild(text);

    private void OpenLink(string token)
    {
        try
        {
            if (token.StartsWith("https://") || token.StartsWith("http://"))
                Process.Start(new ProcessStartInfo(token) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open link: " + ex.Message); }
    }

    // ---------------------------------------------------------------- find

    public void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    public void HideFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _view.ClearSearch();
        _view.Focus();
    }

    private void RunSearch()
    {
        _view.Search(_findBox.Text, caseSensitive: false);
        if (_findBox.Text.Length > 1) _view.FindNext(false);
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        var count = _view.SearchText.Length == 0 ? 0 : _view.MatchCount;
        _findCount.Text = count == 0 ? "no results" : $"{_view.CurrentMatch} of {count}";
    }

    // ---------------------------------------------------------------- misc

    public void FocusTerminal() => _view.Focus();

    // ---------------------------------------------------------------- broadcast indicator

    private Border? _broadcastFrame;

    /// <summary>
    /// Marks this pane as armed for broadcast input. Deliberately a frame and not a badge:
    /// anything drawn inside the pane covers terminal text.
    /// </summary>
    public void ShowBroadcastBadge(bool on)
    {
        if (_broadcastFrame is null)
        {
            _broadcastFrame = new Border
            {
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                ToolTip = "Keystrokes typed here are sent to every pane in this tab",
            };
            // Theme-dependent: red in dark/light, window text in high contrast (no hue-only signal).
            _broadcastFrame.SetResourceReference(Border.BorderBrushProperty, "Brush.Danger");
            Children.Add(_broadcastFrame);
        }

        _broadcastFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- zmodem

    /// <summary>
    /// Takes the stream over for a ZMODEM transfer. Receiving is automatic: that is what
    /// "sz file" on the far end looks like on the wire. Sending is armed from the menu,
    /// because a sender also has to know which local file to offer.
    /// </summary>
    private void StartZmodem(ZmodemRole role, string? sendPath)
    {
        if (_zmodem is not null) return;

        var folder = string.IsNullOrWhiteSpace(_settings.ZmodemDownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : _settings.ZmodemDownloadFolder!;

        ZmodemSession session;
        try
        {
            session = new ZmodemSession(role, WriteToChild, folder, sendPath);
        }
        catch (Exception ex)
        {
            _buffer.AppendNotice("ZMODEM could not start: " + ex.Message);
            return;
        }

        _zmodem = session;
        _zmodemPercent = -10;

        session.Notice += text => Dispatcher.BeginInvoke(() => _buffer.AppendNotice(text));
        session.Changed += _ => Dispatcher.BeginInvoke(UpdateZmodemActivity);
        session.Completed += (ok, text) => Dispatcher.BeginInvoke(() =>
        {
            Activity = null;
            _zmodemPercent = -10;
            _buffer.AppendNotice("ZMODEM: " + text);
            var finished = _zmodem;
            _zmodem = null;
            finished?.Dispose();
            StateChanged?.Invoke(this);
        });

        _buffer.AppendNotice(role == ZmodemRole.Receive
            ? "ZMODEM transfer detected — receiving into " + folder
            : "ZMODEM send armed — run rz on the far end.");
        UpdateZmodemActivity();
        StateChanged?.Invoke(this);
    }

    /// <summary>Arms a ZMODEM send; the far end then runs rz.</summary>
    public void SendFileOverZmodem(string path)
    {
        if (_zmodem is not null)
        {
            _buffer.AppendNotice("A ZMODEM transfer is already running in this tab.");
            return;
        }
        if (State != SessionState.Connected)
        {
            _buffer.AppendNotice("Connect this session before sending a file with ZMODEM.");
            return;
        }
        StartZmodem(ZmodemRole.Send, path);
    }

    public bool ZmodemActive => _zmodem is not null;

    public void CancelZmodem() => _zmodem?.Abort();

    private void UpdateZmodemActivity()
    {
        var session = _zmodem;
        if (session is null) { Activity = null; return; }

        if (session.Total > 0)
        {
            var pct = (int)(session.Bytes * 100 / session.Total);
            Activity = $"ZMODEM {session.CurrentName} {pct}% ({FormatSize(session.Bytes)} of {FormatSize(session.Total)})";
            if (pct / 10 != _zmodemPercent / 10)
            {
                _zmodemPercent = pct;
                StateChanged?.Invoke(this);
            }
        }
        else Activity = $"ZMODEM {session.CurrentName} {FormatSize(session.Bytes)}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
    }

    public void Dispose()
    {
        StopRecording();
        if (_disposed) return;
        _disposed = true;
        _retryTimer?.Stop();
        _retryTimer = null;
        _stableTimer?.Stop();
        _stableTimer = null;
        _zmodem?.Abort("cancelled: the tab closed");
        _zmodem?.Dispose();
        _zmodem = null;
        DisposePty();
        if (_tmux is not null)
        {
            _tmux.Event -= OnTmuxEvent;
            _tmux = null;
            _tmuxBridge = null;
        }
        _view.SetConnected(false);
    }
}
