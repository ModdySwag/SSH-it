using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;
using SshIt.Views;

namespace SshIt;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly DataStore _store;
    private readonly List<TabEntry> _tabs = new();

    /// <summary>Every live pane in tab order; the agent bridge reads session state from these.</summary>
    internal IEnumerable<TerminalPane> AllPanes()
    {
        foreach (var tab in _tabs)
            foreach (var pane in tab.Panes)
                yield return pane;
    }
    private TabEntry? _active;
    private UserControl? _toolView;
    private ToolKind _tool = ToolKind.Files;

    /// <summary>This window's saved slot. Null means the primary window (legacy settings fields).</summary>
    private WindowLayout? _layout;

    private bool _reallyClose;
    private double _zoom = 1.0;
    private Session? _selectedSession;

    private enum ToolKind { Files, Tunnels, Info }

    private sealed class TabEntry
    {
        public required Session Session { get; init; }
        public required TerminalPane Pane { get; set; }
        public required Border Header { get; init; }
        public required TextBlock TitleText { get; init; }
        public required Ellipse Dot { get; init; }

        /// <summary>The dot's row: an optional colour-tag strip sits before the dot.</summary>
        public required StackPanel Marker { get; init; }

        /// <summary>Every pane in this tab: one normally, two when split.</summary>
        public List<TerminalPane> Panes { get; } = new();

        /// <summary>What the tab host shows: a single pane, or a split grid.</summary>
        public Grid Host { get; set; } = new();

        public bool SplitVertical { get; set; } = true;

        /// <summary>Keys typed in one pane are mirrored into the other panes of this tab.</summary>
        public bool Broadcast { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();

        // One clock for the whole window: frame intervals feed the Performance panel, and the
        // first frame is also the honest end of "startup".
        System.Windows.Media.CompositionTarget.Rendering += (_, _) => PerfMetrics.Frame();

        // The panel's memory row: the app's own working set is cheap, but every session also pays
        // for an ssh.exe (and conhost), so the pane list is the only honest source for that number.
        PerfMetrics.Children = () =>
        {
            var ids = new HashSet<int>();
            foreach (var tab in _tabs)
                foreach (var pane in tab.Panes)
                    if (pane.PtyProcessId > 0) ids.Add(pane.PtyProcessId);
            long bytes = 0;
            var count = 0;
            foreach (var id in ids)
            {
                try
                {
                    using var child = System.Diagnostics.Process.GetProcessById(id);
                    bytes += child.WorkingSet64;
                    count++;
                }
                catch { }
            }
            return (count, bytes);
        };

        _app = (App)Application.Current;
        _store = _app.Store;

        ApplyOpacity(_store.Settings.WindowOpacity, announce: false);
        Announcer.Enable(StatusAnnounce);
        PreviewMouseWheel += OnOpacityWheel;
        _pinned = _store.Settings.PinToDesktop;
        BuildMenuExtras();
        RebuildTree();
        UpdateEmptyState();
        UpdateStatus();
        UpdateToolButtons();
        ShowTool(ToolKind.Files);

        SessionSearch.TextChanged += (_, _) => SessionSearchHint.Visibility =
            string.IsNullOrEmpty(SessionSearch.Text) ? Visibility.Visible : Visibility.Collapsed;

        QuickConnectBox.TextChanged += (_, _) => QuickConnectHint.Visibility =
            string.IsNullOrEmpty(QuickConnectBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            // A window restored with tabs selected one before Show(), when Focus() cannot take.
            // Put the caret where the status line points: a restored tab is opened with Enter.
            if (_active is { Pane.IsIdle: true } tab) tab.Pane.FocusTerminal();
        };

        _store.SessionsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            RebuildTree();
            UpdateEmptyState();
            foreach (var tab in _tabs) UpdateTabTag(tab);
        });

    }

    // ---------------------------------------------------------------- launch

    /// <summary>
    /// Everything that depends on how this window was launched: placement, restored tabs, a
    /// session named on the command line, and - for the first window only - the smoke hooks.
    /// App calls this once, before Show, for every window it creates.
    /// </summary>
    public void ApplyLaunch(WindowLayout? layout, IEnumerable<string>? tabIds, string? pendingSessionId,
                            bool restoreTabs, MainWindow? cascadeFrom, bool hooks)
    {
        _layout = layout;
        RestorePlacement();

        if (cascadeFrom is not null)
        {
            var area = SystemParameters.WorkArea;
            var origin = double.IsNaN(cascadeFrom.Left)
                ? new Rect(area.Left, area.Top, cascadeFrom.Width, cascadeFrom.Height)
                : new Rect(cascadeFrom.Left, cascadeFrom.Top, cascadeFrom.Width, cascadeFrom.Height);
            var next = WindowCascade.Next(origin, area, Math.Max(MinWidth, Width), Math.Max(MinHeight, Height));
            Left = next.Left;
            Top = next.Top;
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        }

        if (restoreTabs && tabIds is not null)
        {
            // Lazy restore: bring the tabs back, do not dial eight hosts before the first keystroke.
            // Each pane keeps its Connect affordance, and an explicit --connect below still dials.
            var restored = 0;
            foreach (var id in tabIds.Take(8))
            {
                var session = _store.Sessions.FirstOrDefault(s => s.Id == id);
                if (session is null) continue;
                OpenSession(session, forceNewTab: true, connect: false);
                restored++;
            }
            if (restored > 0 && string.IsNullOrEmpty(pendingSessionId))
                StatusMessage.Text = "Restored " + restored + " tab(s) — press Enter or click Connect in the pane to open it.";
        }

        if (pendingSessionId is { Length: > 0 })
        {
            var target = _store.Sessions.FirstOrDefault(s => s.Id == pendingSessionId);
            if (target is not null) OpenSession(target);
        }

        if (hooks) RunLaunchHooks();
    }

    /// <summary>True for the window the legacy placement fields describe.</summary>
    public bool IsPrimary => _layout is null;

    /// <summary>The settings slot this window owns; null for the primary window.</summary>
    public WindowLayout? Layout => _layout;

    /// <summary>Takes over the primary slot when the original primary window closes first.</summary>
    public void BecomePrimary()
    {
        if (_layout is null) return;
        var slot = _layout;
        _layout = null;

        var s = _store.Settings;
        s.WindowLeft = slot.Left;
        s.WindowTop = slot.Top;
        s.WindowWidth = slot.Width;
        s.WindowHeight = slot.Height;
        s.WindowMaximized = slot.Maximized;
        s.OpenTabSessionIds = slot.TabSessionIds.Distinct().ToList();
        _store.SaveSettings();
    }

    public bool HasSession(string id) => _tabs.Any(t => t.Session.Id == id);

    public void OpenSessionById(string id)
    {
        var session = _store.Sessions.FirstOrDefault(s => s.Id == id);
        if (session is not null) OpenSession(session);
    }

    public void OpenNewSessionDialog() => NewSession(null);

    public void OpenOptionsDialog() => OpenOptions();

    /// <summary>Re-applies the settings every window shares (font, palette, menus).</summary>
    public void ReloadSettings()
    {
        foreach (var tab in _tabs)
        {
            tab.Pane.View.ApplyTheme(TerminalTheme.Get(_store.Settings.TerminalTheme));
            tab.Pane.View.ApplyFont(_store.Settings.FontFamily, _store.Settings.FontSize);
            tab.Pane.View.ApplyHighlights(_store.Settings.HighlightRules);
        }
        BuildMenuExtras();
    }

    /// <summary>Writes this window's tab list into the slot it owns.</summary>
    private void SaveTabs()
    {
        if (!_store.Settings.ReopenTabs) return;
        var ids = _tabs.Select(t => t.Session.Id).Distinct().ToList();
        if (_layout is null) _store.Settings.OpenTabSessionIds = ids;
        else _layout.TabSessionIds = ids;
        _store.SaveSettings();
    }

    private void RunLaunchHooks()
    {
        // Deterministic smoke hook: open a session, then split it.
        if (Environment.GetCommandLineArgs().Contains("--split"))
        {
            var splitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            splitTimer.Tick += (_, _) =>
            {
                splitTimer.Stop();
                if (_active is not null) SplitPane(vertical: true);
            };
            splitTimer.Start();
        }
        // Deterministic smoke hooks (see CONTINUATION notes: no SendKeys, no focus games).
        var rawArgs = Environment.GetCommandLineArgs();
        string? HookValue(string name)
        {
            var index = Array.IndexOf(rawArgs, name);
            return index >= 0 && index + 1 < rawArgs.Length ? rawArgs[index + 1] : null;
        }

        var bcastCommand = HookValue("--bcast");
        var ptyCommand = HookValue("--ptycmd");
        var ptyWrite = HookValue("--ptywrite");
        if (bcastCommand is not null || ptyCommand is not null || ptyWrite is not null)
        {
            var hookTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            hookTimer.Tick += (_, _) =>
            {
                hookTimer.Stop();
                if (_active is null) return;
                var first = _active.Panes[0];
                first.FocusTerminal();
                if (bcastCommand is not null && _active.Panes.Count > 1)
                {
                    SetBroadcast(_active, true);
                    first.SendCommand(bcastCommand);
                }
                else if (ptyCommand is not null) first.SendCommand(ptyCommand);
                else if (ptyWrite is not null) first.SendText(ptyWrite);
            };
            hookTimer.Start();
        }
        // Deterministic smoke hook for the recording player.
        if (rawArgs.Contains("--tearoff"))
        {
            var tearTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            tearTimer.Tick += (_, _) =>
            {
                tearTimer.Stop();
                TearOffTab(_active);
            };
            tearTimer.Start();
        }

        if (rawArgs.Contains("--info"))
        {
            var infoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            infoTimer.Tick += (_, _) =>
            {
                infoTimer.Stop();
                ShowTool(ToolKind.Info);
            };
            infoTimer.Start();
        }

        // Deterministic smoke hook for the command palette (R3-23). --palette-query also opens it,
        // pre-filled, so a screenshot proves the ranking without driving the keyboard.
        var paletteQuery = HookValue("--palette-query");
        if (rawArgs.Contains("--palette") || paletteQuery is not null)
        {
            var paletteTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            paletteTimer.Tick += (_, _) =>
            {
                paletteTimer.Stop();
                OpenPalette(paletteQuery);
            };
            paletteTimer.Start();
        }

        var castIndex = Array.IndexOf(rawArgs, "--cast");
        if (castIndex >= 0 && castIndex + 1 < rawArgs.Length)
        {
            var castPath = rawArgs[castIndex + 1];
            var castTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            castTimer.Tick += (_, _) =>
            {
                castTimer.Stop();
                OpenRecording(castPath);
            };
            castTimer.Start();
        }
    }

    // ---------------------------------------------------------------- window chrome

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        WindowEffects.ApplyDarkMode(handle, _app.Theme.IsDark);
        _app.Theme.Changed += () =>
        {
            WindowEffects.ApplyDarkMode(handle, _app.Theme.IsDark);
            ApplyThemeToPanes();
        };
        UpdateMaximizeGlyph();
        if (_pinned) SetPinned(true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_GETMINMAXINFO = 0x0024;
        const int HTMAXBUTTON = 10;

        if (msg == WM_GETMINMAXINFO)
        {
            WindowEffects.ClampMaximizedSize(hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }

        const int WM_ACTIVATE = 0x0006;

        if (msg == WM_ACTIVATE && _pinned && (wParam.ToInt64() & 0xFFFF) == 0)
        {
            SinkToDesktop(hwnd);
        }

        if (msg == WM_NCHITTEST && WindowState == WindowState.Normal)
        {
            var screenPoint = new Point(
                (short)(lParam.ToInt64() & 0xFFFF),
                (short)((lParam.ToInt64() >> 16) & 0xFFFF));
            if (MaximizeButton.IsVisible && IsOver(MaximizeButton, screenPoint))
            {
                handled = true;
                return (IntPtr)HTMAXBUTTON;
            }
        }
        return IntPtr.Zero;
    }

    private static bool IsOver(FrameworkElement element, Point screenPoint)
    {
        try
        {
            var p = element.PointFromScreen(screenPoint);
            return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
        }
        catch { return false; }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); } catch { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => System.Windows.SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) System.Windows.SystemCommands.RestoreWindow(this);
        else System.Windows.SystemCommands.MaximizeWindow(this);
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore down" : "Maximize";
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeGlyph();
    }

    private void RestorePlacement()
    {
        var s = _store.Settings;

        var left = _layout?.Left ?? s.WindowLeft;
        var top = _layout?.Top ?? s.WindowTop;
        var width = _layout?.Width ?? s.WindowWidth;
        var height = _layout?.Height ?? s.WindowHeight;
        var maximized = _layout?.Maximized ?? s.WindowMaximized;

        if (!double.IsNaN(left) && !double.IsNaN(top))
        {
            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                         SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var candidate = new Rect(left, top, width, height);
            if (virtualScreen.IntersectsWith(candidate))
            {
                Left = left;
                Top = top;
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            }
        }
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        if (maximized) WindowState = WindowState.Maximized;

        SidebarColumn.Width = new GridLength(s.SidebarVisible ? Math.Max(180, s.SidebarWidth) : 0);
        Sidebar.Visibility = s.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        MenuSidebar.IsChecked = s.SidebarVisible;

        RightPanelColumn.Width = new GridLength(s.RightPanelVisible ? Math.Max(280, s.RightPanelWidth) : 0);
        RightPanel.Visibility = s.RightPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = RightPanel.Visibility;
        MenuRightPanel.IsChecked = s.RightPanelVisible;

        StatusBar.Visibility = s.StatusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        MenuStatusBar.IsChecked = s.StatusBarVisible;
    }

    private void SavePlacement()
    {
        var s = _store.Settings;

        if (_layout is null)
        {
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            s.WindowMaximized = WindowState == WindowState.Maximized;
        }
        else
        {
            if (WindowState == WindowState.Normal)
            {
                _layout.Left = Left;
                _layout.Top = Top;
                _layout.Width = Width;
                _layout.Height = Height;
            }
            _layout.Maximized = WindowState == WindowState.Maximized;
        }

        if (!_focusMode)
        {
            s.SidebarVisible = Sidebar.Visibility == Visibility.Visible;
            s.RightPanelVisible = RightPanel.Visibility == Visibility.Visible;
            s.StatusBarVisible = StatusBar.Visibility == Visibility.Visible;
            if (s.SidebarVisible) s.SidebarWidth = SidebarColumn.Width.Value;
            if (s.RightPanelVisible) s.RightPanelWidth = RightPanelColumn.Width.Value;
        }
        _store.SaveSettings();
    }

    // ---------------------------------------------------------------- menu extras

    private void BuildMenuExtras()
    {
        BuildThemeMenu();
        BuildWorkspaceMenu();
        MenuTerminalColours.Items.Clear();
        foreach (var theme in TerminalTheme.BuiltIn)
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                IsCheckable = true,
                IsChecked = string.Equals(theme.Name, _store.Settings.TerminalTheme, StringComparison.OrdinalIgnoreCase)
            };
            var name = theme.Name;
            item.Click += (_, _) =>
            {
                _store.Settings.TerminalTheme = name;
                _store.SaveSettings();
                foreach (var t in _tabs) t.Pane.View.ApplyTheme(TerminalTheme.Get(name));
                BuildMenuExtras();
            };
            MenuTerminalColours.Items.Add(item);
        }

        RefreshThemeChecks();
    }

    private void RefreshThemeChecks()
    {
        // High contrast wins at render time, so it is the one that reads as checked; the stored
        // preference is what comes back when it stops winning.
        var checkedId = _app.Theme.HighContrast ? "HighContrast" : _store.Settings.Theme;

        foreach (var item in MenuTheme.Items.OfType<MenuItem>())
        {
            var id = item.Tag as string;
            item.IsChecked = string.Equals(id, checkedId, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(id, "HighContrast", StringComparison.OrdinalIgnoreCase))
                item.ToolTip = SystemParameters.HighContrast
                    ? "Windows high contrast is on, so this theme is in use until you turn it off."
                    : null;
        }
    }

    /// <summary>Re-themes after the system high-contrast setting (or its colours) changed.</summary>
    public void RefreshHighContrast()
    {
        ApplyThemeToPanes();
        RefreshThemeChecks();
        BuildMenuExtras();
    }

    private void ApplyThemeToPanes()
    {
        foreach (var tab in _tabs) tab.Pane.View.ApplyTheme(TerminalTheme.Get(_store.Settings.TerminalTheme));
    }

    // ---------------------------------------------------------------- session tree

    private void RebuildTree()
    {
        SessionTree.Items.Clear();
        var filter = SessionSearch.Text.Trim();
        var sessions = _store.Sessions;

        if (!string.IsNullOrEmpty(filter))
        {
            var matches = sessions
                .Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || s.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || s.User.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || s.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var session in matches) SessionTree.Items.Add(CreateSessionNode(session));
            SessionCountText.Text = $"{matches.Count} of {sessions.Count} sessions";
            return;
        }

        var folders = _store.Folders;
        var byFolder = sessions.GroupBy(s => s.FolderId ?? "").ToDictionary(g => g.Key, g => g.ToList());

        foreach (var folder in folders.Where(f => f.ParentId is null)
                                      .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            SessionTree.Items.Add(CreateFolderNode(folder, byFolder));
        }

        if (byFolder.TryGetValue("", out var loose))
        {
            foreach (var session in loose.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                SessionTree.Items.Add(CreateSessionNode(session));
        }

        SessionCountText.Text = sessions.Count == 1 ? "1 session" : $"{sessions.Count} sessions";
    }

    private TreeViewItem CreateFolderNode(SessionFolder folder, Dictionary<string, List<Session>> byFolder)
    {
        var item = new TreeViewItem
        {
            Header = BuildFolderHeader(folder),
            Tag = folder,
            IsExpanded = folder.IsExpanded
        };
        item.Expanded += (_, _) => folder.IsExpanded = true;
        item.Collapsed += (_, _) => folder.IsExpanded = false;

        foreach (var child in _store.Folders.Where(f => f.ParentId == folder.Id)
                                            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            item.Items.Add(CreateFolderNode(child, byFolder));

        if (byFolder.TryGetValue(folder.Id, out var sessions))
            foreach (var session in sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                item.Items.Add(CreateSessionNode(session));

        item.ContextMenu = BuildFolderMenu(folder);
        return item;
    }

    private static StackPanel BuildFolderHeader(SessionFolder folder)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "\uE8B7",
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = folder.Name,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private TreeViewItem CreateSessionNode(Session session)
    {
        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };

        // A session's colour tag, before its icon: the tree is the one place every session is
        // visible at once, so a colour has to be readable there or it is decoration.
        if (CreateTagStrip(session.ColorTag, 13) is { } tagStrip) title.Children.Add(tagStrip);

        title.Children.Add(new TextBlock
        {
            Text = "\uE756",
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        title.Children.Add(new TextBlock
        {
            Text = session.Name,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(title);

        panel.Children.Add(new TextBlock
        {
            Text = session.Subtitle,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(18, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var item = new TreeViewItem { Header = panel, Tag = session };
        System.Windows.Automation.AutomationProperties.SetName(item, session.Name + " — " + session.Subtitle);
        item.MouseDoubleClick += (_, e) => { OpenSession(session); e.Handled = true; };
        item.ContextMenu = BuildSessionMenu(session);
        return item;
    }

    private ContextMenu BuildSessionMenu(Session session)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Open", () => OpenSession(session), "Enter"));
        menu.Items.Add(Item("Open in new tab", () => OpenSession(session, forceNewTab: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Edit…", () => EditSession(session), "F2"));
        menu.Items.Add(Item("Duplicate", () => DuplicateSession(session)));
        menu.Items.Add(Item("Copy ssh command", () => CopySshCommand(session)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move to folder", () => MoveSessionToFolder(session)));
        menu.Items.Add(Item("Delete", () => DeleteSession(session), "Del"));
        return menu;
    }

    private ContextMenu BuildFolderMenu(SessionFolder folder)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("New session here…", () => NewSession(folder.Id)));
        menu.Items.Add(Item("New subfolder…", () => NewFolder(folder.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rename…", () => RenameFolder(folder)));
        menu.Items.Add(Item("Delete folder", () => DeleteFolder(folder)));
        return menu;
    }

    private void SessionTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (SessionTree.SelectedItem is TreeViewItem { Tag: Session session })
        {
            _selectedSession = session;
            _store.Settings.LastSelectedSessionId = session.Id;
        }
        else if (SessionTree.SelectedItem is TreeViewItem { Tag: SessionFolder folder })
        {
            _selectedSession = null;
            folder.IsExpanded = !folder.IsExpanded;
        }
    }

    private void SessionTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var item = FindAncestor<TreeViewItem>(source);
            if (item is not null) item.IsSelected = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void SessionSearch_TextChanged(object sender, TextChangedEventArgs e) => RebuildTree();

    // ---------------------------------------------------------------- tabs

    private void OpenSession(Session session, bool forceNewTab = false, string? secret = null, bool connect = true)
    {
        // A tmux session is a control-mode client with its own window: it has no pty, no SFTP and
        // no ledger, and pretending it is a tab would hand the user a shell that is not tmux.
        if (string.Equals(session.Kind, "Tmux", StringComparison.OrdinalIgnoreCase))
        {
            OpenTmuxWindow(session);
            return;
        }

        var existing = _tabs.FirstOrDefault(t => t.Session.Id == session.Id);
        if (existing is not null && !forceNewTab)
        {
            SelectTab(existing);
            existing.Pane.FocusTerminal();
            return;
        }

        var pane = new TerminalPane(session, _store.Settings);
        // Every pane subscribes once, where it is born. SplitPane subscribes only the new pane, so
        // without this the first pane of a split tab never becomes the active one again.
        pane.GotKeyboardFocus += PaneFocusChanged;
        AttachBroadcast(pane);
        pane.WorkingDirectoryChanged += (p, path) =>
        {
            // The Files panel mirrors the shell of the tab that is in front, and only that one.
            if (p != _active?.Panes[0]) return;
            if (_toolView is SftpPanel files) files.FollowShell(path);
        };
        pane.StateChanged += _ =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateTabHeader(pane);
                UpdateStatus();
                // The Files dock waits for the pane's child before it dials its own sftp; a lazy
                // restored tab must not produce background ssh traffic just because it was selected.
                if (ReferenceEquals(pane, _active?.Panes[0])) RefreshToolSession();
                // R3-24: connect and fail are worth saying out loud; "connecting" on every retry is
                // not. Only the pane the user is looking at speaks.
                var state = pane.State;
                if ((state is SessionState.Connected or SessionState.Failed)
                    && ReferenceEquals(pane, _active?.Pane))
                {
                    Announcer.Announce(StatusAnnounce,
                        Announcer.StateAnnouncement(pane.Session.Name, state, pane.LastError));
                }
            });
        };
        pane.BlockCompleted += mark => AnnounceBlock(pane, mark);
        pane.OutputArrived += p => Dispatcher.BeginInvoke(() => MarkTabActivity(p));
        pane.DiagnoseRequested += (p, context) =>
            new Views.DiagnoseWindow(p.Session, context) { Owner = this }.ShowDialog();
        pane.ApproveRequested += p => ApproveFor(p);
        pane.TitleChanged += (p, title) => Dispatcher.BeginInvoke(() =>
        {
            // Consoles report their exe path as the title until a shell sets a real one.
            if (string.IsNullOrWhiteSpace(title)) return;
            if (title.Contains(".exe", StringComparison.OrdinalIgnoreCase)) return;
            if (title.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) || title.StartsWith('/')) return;
            var entry = _tabs.FirstOrDefault(t => t.Pane == p);
            if (entry is not null) entry.TitleText.Text = title;
            UpdateTitleText();
        });
        pane.Notice += (p, what) =>
        {
            if (what != "close") return;
            var entry = _tabs.FirstOrDefault(t => t.Pane == p);
            if (entry is not null) CloseTab(entry);
        };

        var header = BuildTabHeader(session, pane, out var titleText, out var dot, out var marker);
        var host = new Grid();
        host.Children.Add(pane);
        var tab = new TabEntry
        {
            Session = session, Pane = pane, Header = header, TitleText = titleText, Dot = dot,
            Marker = marker, Host = host
        };
        tab.Panes.Add(pane);
        _tabs.Add(tab);
        TabStrip.Children.Add(header);

        SelectTab(tab);

        if (connect)
        {
            var stored = CredentialVault.GetSessionSecret(session.Id);
            pane.Connect(secret ?? stored);
            pane.FocusTerminal();
        }

        _store.Settings.AddRecent(session.Id);
        _app.UpdateJumpList();
        UpdateEmptyState();
    }

    private Border BuildTabHeader(Session session, TerminalPane pane, out TextBlock titleText, out Ellipse dot,
                                  out StackPanel marker)
    {
        var header = new Border
        {
            Height = 30,
            Margin = new Thickness(0, 3, 2, 3),
            Padding = new Thickness(10, 0, 4, 0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.FindResource("Brush.TextDisabled")
        };

        // The dot keeps meaning connection state, so the tag rides beside it rather than colouring it:
        // a red dot must still mean "failed", never "production".
        marker = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (CreateTagStrip(session.ColorTag, 14) is { } strip) marker.Children.Add(strip);
        marker.Children.Add(dot);
        Grid.SetColumn(marker, 0);
        grid.Children.Add(marker);

        titleText = new TextBlock
        {
            Text = session.Name,
            FontSize = 12,
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);
        grid.Children.Add(titleText);

        var close = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE711",
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
                FontSize = 8
            },
            Width = 20,
            Height = 20,
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Btn.Icon"),
            Visibility = Visibility.Collapsed,
            ToolTip = "Close tab"
        };
        close.Click += (_, _) =>
        {
            var entry = _tabs.FirstOrDefault(t => t.Panes.Contains(pane));
            if (entry is not null) CloseTab(entry);
        };
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        header.Child = grid;
        header.MouseEnter += (_, _) =>
        {
            close.Visibility = Visibility.Visible;
            if (_tabs.FirstOrDefault(t => t.Header == header) != _active)
                header.Background = (Brush)Application.Current.FindResource("Brush.TabHover");
        };
        header.MouseLeave += (_, _) =>
        {
            close.Visibility = Visibility.Collapsed;
            if (_tabs.FirstOrDefault(t => t.Header == header) != _active)
                header.Background = Brushes.Transparent;
        };
        header.MouseLeftButtonDown += (_, _) =>
        {
            var entry = _tabs.FirstOrDefault(t => t.Header == header);
            if (entry is not null) SelectTab(entry);
        };
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            var entry = _tabs.FirstOrDefault(t => t.Header == header);
            if (entry is not null) CloseTab(entry);
        };

        return header;
    }

    /// <summary>
    /// The colour bar a tagged session shows in the tree and on its tab. One helper so the two places
    /// cannot drift, and null when the tag is absent or unusable.
    /// </summary>
    private static Border? CreateTagStrip(string? colorTag, double height)
    {
        if (SessionTag.Brush(colorTag) is not { } brush) return null;
        return new Border
        {
            Width = 3,
            Height = height,
            CornerRadius = new CornerRadius(2),
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = SessionTag.Name(colorTag) + " tag"
        };
    }

    /// <summary>
    /// Re-reads a session's colour into the tab that is already open. Editing a session rebuilds the
    /// tree, but a tab header is built once — without this a colour picked now would only appear on
    /// the next launch.
    /// </summary>
    private void UpdateTabTag(TabEntry entry)
    {
        while (entry.Marker.Children.Count > 1) entry.Marker.Children.RemoveAt(0);
        if (CreateTagStrip(entry.Session.ColorTag, 14) is { } strip) entry.Marker.Children.Insert(0, strip);
    }

    private void SelectTab(TabEntry entry)
    {
        _active = entry;
        foreach (var tab in _tabs)
        {
            var isActive = tab == entry;
            tab.Header.Background = (Brush)Application.Current.FindResource(isActive ? "Brush.TabActive" : "Brush.TabInactive");
            tab.Header.BorderBrush = (Brush)Application.Current.FindResource("Brush.Border");
            tab.Header.BorderThickness = isActive ? new Thickness(0, 0, 0, 2) : new Thickness(0);
            tab.TitleText.Foreground = (Brush)Application.Current.FindResource(isActive ? "Brush.Text" : "Brush.TextMuted");
            if (isActive) tab.TitleText.FontWeight = FontWeights.Normal;   // reading it clears the marker
        }

        TerminalHost.Content = entry.Host;
        UpdateBroadcastMenu();
        EmptyState.Visibility = Visibility.Collapsed;
        entry.Pane.FocusTerminal();
        // The content swap above realises the new host on the next layout pass, so Focus() in this
        // frame can land on an element that is not in the visual tree yet; the click then leaves the
        // keyboard in the QuickConnect box and the "press Enter" hint is a lie. Ask again once it exists.
        Dispatcher.BeginInvoke(new Action(entry.Pane.FocusTerminal),
            System.Windows.Threading.DispatcherPriority.Input);
        UpdateTitleText();
        UpdateStatus();
        _store.Settings.LastSelectedSessionId = entry.Session.Id;
        SaveTabs();
        RefreshToolSession();
    }

    private void CloseTab(TabEntry entry)
    {
        if (entry.Panes.Any(p => p.IsLocked))
        {
            StatusMessage.Text = "Unlock the session before closing it.";
            return;
        }
        if (_store.Settings.ConfirmOnClose && entry.Pane.State == SessionState.Connected)
        {
            var result = MessageBox.Show(
                $"Close the session to {entry.Session.DisplayTarget}?",
                "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;
        }

        foreach (var p in entry.Panes) p.Dispose();
        _tabs.Remove(entry);
        TabStrip.Children.Remove(entry.Header);

        if (_active == entry)
        {
            _active = _tabs.LastOrDefault();
            if (_active is not null) SelectTab(_active);
            else
            {
                TerminalHost.Content = null;
                UpdateEmptyState();
            }
        }

        SaveTabs();
        UpdateTitleText();
        UpdateStatus();
    }

    /// <summary>
    /// A background tab that produced output goes bold until you look at it — the one signal a tab
    /// strip can carry when the pane itself is hidden. The active tab never marks itself.
    /// </summary>
    private void MarkTabActivity(TerminalPane pane)
    {
        var entry = _tabs.FirstOrDefault(t => t.Panes.Contains(pane));
        if (entry is null || ReferenceEquals(entry, _active)) return;
        if (entry.TitleText.FontWeight == FontWeights.Bold) return;
        entry.TitleText.FontWeight = FontWeights.Bold;
    }

    private void UpdateTabHeader(TerminalPane pane)
    {
        var entry = _tabs.FirstOrDefault(t => t.Pane == pane);
        if (entry is null) return;
        entry.Dot.Fill = (Brush)Application.Current.FindResource(pane.State switch
        {
            SessionState.Connected => "Brush.Success",
            SessionState.Connecting => "Brush.Warn",
            SessionState.Failed => "Brush.Danger",
            _ => "Brush.TextDisabled"
        });

        // R3-24: the dot is colour only, which a screen reader cannot read. Both the dot and the
        // tab header carry the state as text, so "connected" is heard rather than seen.
        var state = Announcer.StateName(pane.State);
        System.Windows.Automation.AutomationProperties.SetName(entry.Dot, state);
        System.Windows.Automation.AutomationProperties.SetName(entry.Header, entry.TitleText.Text + " — " + state);

        if (pane.State == SessionState.Connected && pane.ConnectMilliseconds > 0)
            entry.Header.ToolTip = $"{pane.Session.DisplayTarget} — connected in {pane.ConnectMilliseconds} ms";
    }

    /// <summary>
    /// R3-24: speaks one finished command. A completion in another tab names its session first so
    /// the listener can tell it apart from the tab in front.
    /// </summary>
    private void AnnounceBlock(TerminalPane pane, PromptMark mark)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AnnounceBlock(pane, mark));
            return;
        }

        var entry = _tabs.FirstOrDefault(t => t.Panes.Contains(pane));
        var otherTab = entry is not null && !ReferenceEquals(entry, _active);
        Announcer.Announce(StatusAnnounce, Announcer.BlockAnnouncement(
            mark.Command, mark.ExitCode, mark.Duration, otherTab ? pane.Session.Name : null));
    }

    private void UpdateTitleText()
    {
        TitleText.Text = _active is null ? "" : $"{_active.Session.Name} — {_active.Session.DisplayTarget}";
        Title = _active is null ? "SSH-it" : $"{_active.Session.Name} — SSH-it";
    }

    private void UpdateEmptyState()
    {
        var empty = _tabs.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        EmptyRecentList.Items.Clear();
        var recent = _store.Settings.RecentSessionIds
            .Select(id => _store.Sessions.FirstOrDefault(s => s.Id == id))
            .Where(s => s is not null)
            .Take(5)
            .ToList();

        EmptyRecentLabel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var session in recent)
        {
            var button = new Button
            {
                Content = $"{session!.Name}   ·   {session.DisplayTarget}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(12, 0, 12, 0),
                Height = 34
            };
            var captured = session;
            button.Click += (_, _) => OpenSession(captured);
            EmptyRecentList.Items.Add(button);
        }
    }

    // ---------------------------------------------------------------- status

    private void UpdateStatus()
    {
        if (_active is null)
        {
            StatusDot.Fill = (Brush)Application.Current.FindResource("Brush.TextDisabled");
            System.Windows.Automation.AutomationProperties.SetName(StatusDot, "not connected");
            StatusText.Text = "Ready";
            StatusHostText.Text = "";
            StatusLatency.Text = "";
            StatusMessage.Text = "";
            return;
        }

        var pane = _active.Pane;
        StatusDot.Fill = (Brush)Application.Current.FindResource(pane.State switch
        {
            SessionState.Connected => "Brush.Success",
            SessionState.Connecting => "Brush.Warn",
            SessionState.Failed => "Brush.Danger",
            _ => "Brush.TextDisabled"
        });
        System.Windows.Automation.AutomationProperties.SetName(StatusDot, Announcer.StateName(pane.State));

        StatusText.Text = pane.State switch
        {
            SessionState.Connecting => "Connecting…",
            SessionState.Connected => "Connected",
            SessionState.Failed => "Connection failed",
            SessionState.Disconnected => "Disconnected",
            _ => "Ready"
        };
        StatusHostText.Text = pane.Session.DisplayTarget;
        StatusLatency.Text = pane.ConnectMilliseconds > 0 ? $"{pane.ConnectMilliseconds} ms" : "";
        StatusMessage.Text = pane.LastError ?? pane.Activity ?? "";
    }

    // ---------------------------------------------------------------- tool panel

    private void ShowTool(ToolKind kind)
    {
        _tool = kind;
        if (RightPanel.Visibility != Visibility.Visible) ToggleRightPanel(true);

        _toolView = kind switch
        {
            ToolKind.Files => new SftpPanel(this, _store, ActiveSession),
            ToolKind.Tunnels => new TunnelPanel(this, _store, ActiveSession),
            _ => new InfoPanel(this, ActiveSession)
        };
        ToolHost.Content = _toolView;
        UpdateToolButtons();
    }

    private void RefreshToolSession()
    {
        if (_toolView is SftpPanel f)
        {
            f.SetSession(ActiveSession, _active?.Panes[0].State == SessionState.Connected);
            f.FollowShell(_active?.Panes[0].WorkingDirectory);
        }
        else if (_toolView is TunnelPanel t) t.SetSession(ActiveSession);
        else if (_toolView is InfoPanel i) i.SetSession(ActiveSession);
    }

    internal Session? ActiveSession => _active?.Session ?? _selectedSession;

    private void UpdateToolButtons()
    {
        var accent = (Brush)Application.Current.FindResource("Brush.Accent");
        var muted = (Brush)Application.Current.FindResource("Brush.TextMuted");
        FilesTabButton.Foreground = _tool == ToolKind.Files ? accent : muted;
        TunnelsTabButton.Foreground = _tool == ToolKind.Tunnels ? accent : muted;
        InfoTabButton.Foreground = _tool == ToolKind.Info ? accent : muted;
    }

    private void ToggleRightPanel(bool? show = null)
    {
        if (_focusMode) ExitFocusMode();
        var visible = show ?? RightPanel.Visibility != Visibility.Visible;
        RightPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = RightPanel.Visibility;
        RightPanelColumn.Width = new GridLength(visible ? Math.Max(280, _store.Settings.RightPanelWidth) : 0);
        MenuRightPanel.IsChecked = visible;
    }

    // ---------------------------------------------------------------- shortcuts

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var focusInTerminal = _active is not null && _active.Pane.IsKeyboardFocusWithin;

        if (ctrl && shift && e.Key == Key.N) { NewWindow_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.P) { OpenPalette(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Z) { ToggleFocusMode(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.L) { LocalTerminal_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.N) { NewSession(null); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.K) { QuickConnect_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.F &&
            Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not PasswordBox)
        {
            Find_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.OemComma) { Options_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.T) { NewTab_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.W) { CloseTab_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.D) { SplitPane(vertical: true); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.E) { SplitPane(vertical: false); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Q) { ClosePane_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.B) { SetBroadcast(_active, !(_active?.Broadcast ?? false)); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Enter) { SendToAllInteractive(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.M) { TearOffTab(_active); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.F) { ShowTool(ToolKind.Files); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.S) { ShowTool(ToolKind.Info); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.K) { _active?.Pane.OpenLockDialog(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.G) { Diagnose_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Tab)
        {
            CycleTab(shift ? -1 : 1);
            e.Handled = true;
            return;
        }
        // Alt suppresses the real key into SystemKey, so e.Key alone never matched a digit
        // and the advertised "Alt+1...9 go to tab" gesture was dead.
        var altKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (alt && altKey >= Key.D1 && altKey <= Key.D9)
        {
            var index = altKey - Key.D1;
            if (index < _tabs.Count) SelectTab(_tabs[index]);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11) { FullScreen_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.F2 && !focusInTerminal) { EditSession(_selectedSession); e.Handled = true; return; }
        if (e.Key == Key.Delete && !focusInTerminal && _selectedSession is not null)
        {
            DeleteSession(_selectedSession);
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { Zoom(0.1); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { Zoom(-0.1); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D0) { ZoomReset(); e.Handled = true; return; }
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Count < 2 || _active is null) return;
        var index = _tabs.IndexOf(_active);
        index = (index + delta + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[index]);
    }

    private void Zoom(double delta)
    {
        _zoom = Math.Clamp(_zoom + delta, 0.7, 2.2);
        foreach (var tab in _tabs)
            tab.Pane.View.ApplyFont(_store.Settings.FontFamily, Math.Round(_store.Settings.FontSize * _zoom, 1));
    }

    // ---------------------------------------------------------------- pin to desktop

    private bool _pinned;

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void PinMenu_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void SetPinned(bool on)
    {
        _pinned = on;
        _store.Settings.PinToDesktop = on;
        try { _store.SaveSettings(); } catch { }

        MenuPin.IsChecked = on;
        PinButton.Content = on ? "\uE840" : "\uE718";
        PinButton.ToolTip = on ? "Pinned to desktop — click to release" : "Pin to desktop";

        if (on) SinkToDesktop(new WindowInteropHelper(this).Handle);
        StatusMessage.Text = on
            ? "Pinned to desktop — other windows stay in front; click the pin to release."
            : "Unpinned from the desktop.";
    }

    /// <summary>Puts the window behind every other top-level window without moving it.</summary>
    private static void SinkToDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // ---------------------------------------------------------------- window opacity

    /// <summary>Ctrl+scroll fades the whole window so text behind it can be read and typed in.</summary>
    private void OnOpacityWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        // Ctrl+wheel is the terminal convention for zoom; fading the window moves to Ctrl+Shift+wheel,
        // where it cannot be hit by accident while reading.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            Zoom(e.Delta > 0 ? 0.1 : -0.1);
            StatusMessage.Text = "Zoom " + _zoom.ToString("0.0") + "× — Ctrl+Shift+wheel fades the window.";
        }
        else
        {
            ApplyOpacity(Opacity + (e.Delta > 0 ? 0.05 : -0.05));
        }
        e.Handled = true;
    }

    private void ResetOpacity_Click(object sender, RoutedEventArgs e) => ApplyOpacity(1.0);

    private void ApplyOpacity(double value, bool announce = true)
    {
        var clamped = Math.Clamp(Math.Round(value, 2), 0.30, 1.00);
        if (Math.Abs(clamped - Opacity) > 0.001) Opacity = clamped;

        _store.Settings.WindowOpacity = clamped;
        try { _store.SaveSettings(); } catch { }

        if (announce)
        {
            StatusMessage.Text = clamped >= 0.999
                ? "Window opacity 100%."
                : $"Window opacity {clamped:P0} — Ctrl+scroll to adjust, View > Reset window opacity to restore.";
        }
    }

    private void ZoomReset()
    {
        _zoom = 1.0;
        foreach (var tab in _tabs)
            tab.Pane.View.ApplyFont(_store.Settings.FontFamily, _store.Settings.FontSize);
    }

    // ---------------------------------------------------------------- closing

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClose && _store.Settings.CloseToTray && _app.TrayAvailable)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_store.Settings.ConfirmOnClose)
        {
            var live = _tabs.Count(t => t.Pane.State == SessionState.Connected);
            if (live > 0)
            {
                var many = _app.OpenWindows.Count > 1;
                var message = live == 1
                    ? (many ? "One session in this window is still connected. Close it?"
                            : "One session is still connected. Close SSH-it?")
                    : (many ? $"{live} sessions in this window are still connected. Close it?"
                            : $"{live} sessions are still connected. Close SSH-it?");
                var result = MessageBox.Show(message, "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        SaveTabs();
        SavePlacement();
        foreach (var tab in _tabs) tab.Pane.Dispose();
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();

        // A hidden or background window does not always take the foreground from Activate alone.
        // Pinned windows must keep their z-order, so they are left where they are.
        if (!_pinned)
        {
            Topmost = true;
            Topmost = false;
        }
    }

    public void RequestClose()
    {
        _reallyClose = true;
        Close();
    }
}
