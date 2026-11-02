using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;
using Microsoft.Win32;
using SshIt.Views;

namespace SshIt;

public partial class MainWindow
{
    private bool _fullScreen;

    // ---------------------------------------------------------------- file

    private void NewSession_Click(object sender, RoutedEventArgs e) => NewSession(null);

    private void NewSession(string? folderId)
    {
        var draft = new Session { FolderId = folderId, Name = "" };
        var editor = new SessionEditorWindow(draft, _store, isNew: true) { Owner = this };
        if (editor.ShowDialog() != true) return;

        _store.Sessions.Add(editor.Session);
        _store.SaveSessions();
        RebuildTree();
        UpdateEmptyState();
        SelectSessionInTree(editor.Session);
        StatusMessage.Text = $"Added {editor.Session.Name}";
        _app.UpdateJumpList();
    }

    private void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        QuickConnectBox.Focus();
        QuickConnectBox.SelectAll();
    }

    private void LocalTerminal_Click(object sender, RoutedEventArgs e)
    {
        var session = new Session
        {
            Name = "PowerShell",
            IsLocal = true,
            Host = "localhost",
            MonitorEnabled = false
        };
        OpenSession(session, forceNewTab: true);
    }

    private void QuickConnectBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { QuickConnectGo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { QuickConnectBox.Text = ""; _active?.Pane.FocusTerminal(); e.Handled = true; }
    }

    private void QuickConnectGo_Click(object sender, RoutedEventArgs e)
    {
        var session = SshLauncher.ParseQuickConnect(QuickConnectBox.Text);
        if (session is null)
        {
            StatusMessage.Text = "Type a host as user@host or ssh://user@host:port";
            QuickConnectBox.Focus();
            return;
        }

        session.MonitorEnabled = false;
        QuickConnectBox.Text = "";
        OpenSession(session);
    }

    private void ImportSshConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(SshConfigParser.ConfigPath))
        {
            MessageBox.Show(this, "No ~/.ssh/config file was found.", "SSH-it",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var imported = SshConfigParser.ToSessions();
        if (imported.Count == 0)
        {
            MessageBox.Show(this, "The config file contains no usable Host entries.", "SSH-it",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var added = 0;
        foreach (var session in imported)
        {
            var exists = _store.Sessions.Any(s =>
                string.Equals(s.Host, session.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.User, session.User, StringComparison.OrdinalIgnoreCase) &&
                s.Port == session.Port);
            if (exists) continue;
            _store.Sessions.Add(session);
            added++;
        }

        _store.SaveSessions();
        RebuildTree();
        MessageBox.Show(this, $"Imported {added} of {imported.Count} host(s) from ~/.ssh/config.",
            "SSH-it", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ImportPutty_Click(object sender, RoutedEventArgs e)
    {
        var imported = PuttyImporter.Import();
        if (imported.Count == 0)
        {
            MessageBox.Show(this, "No saved PuTTY sessions were found in the registry.", "SSH-it",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var added = 0;
        foreach (var session in imported)
        {
            if (_store.Sessions.Any(s => string.Equals(s.Host, session.Host, StringComparison.OrdinalIgnoreCase)
                                         && s.Port == session.Port)) continue;
            _store.Sessions.Add(session);
            added++;
        }
        _store.SaveSessions();
        RebuildTree();
        MessageBox.Show(this, $"Imported {added} PuTTY session(s).", "SSH-it",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed class ExportBundle
    {
        public int Version { get; set; } = 1;
        public string Product { get; set; } = "SSH-it";
        public List<Session> Sessions { get; set; } = new();
        public List<SessionFolder> Folders { get; set; } = new();
    }

    private void ExportSessions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export sessions",
            Filter = "SSH-it sessions (*.ssh-it.json)|*.ssh-it.json|JSON (*.json)|*.json",
            FileName = "ssh-it-sessions.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        var bundle = new ExportBundle { Sessions = _store.Sessions, Folders = _store.Folders };
        File.WriteAllText(dialog.FileName,
            JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));
        StatusMessage.Text = $"Exported {_store.Sessions.Count} session(s).";
    }

    private void ImportSessions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import sessions",
            Filter = "SSH-it sessions (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var bundle = JsonSerializer.Deserialize<ExportBundle>(File.ReadAllText(dialog.FileName));
            if (bundle is null) return;

            var folderMap = new Dictionary<string, string>();
            foreach (var folder in bundle.Folders)
            {
                if (_store.Folders.Any(f => f.Id == folder.Id)) { folderMap[folder.Id] = folder.Id; continue; }
                _store.Folders.Add(folder);
                folderMap[folder.Id] = folder.Id;
            }

            var added = 0;
            foreach (var session in bundle.Sessions)
            {
                if (session.FolderId is not null && folderMap.TryGetValue(session.FolderId, out var mapped))
                    session.FolderId = mapped;
                session.Id = Guid.NewGuid().ToString("n");
                _store.Sessions.Add(session);
                added++;
            }

            _store.SaveSessions();
            _store.SaveFolders();
            RebuildTree();
            MessageBox.Show(this, $"Imported {added} session(s).", "SSH-it",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "That file could not be read:\n\n" + ex.Message, "SSH-it",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_store.RootDir) { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage.Text = ex.Message; }
    }

    // ---------------------------------------------------------------- recordings

    private void PlayRecording_Click(object sender, RoutedEventArgs e) => OpenRecording(null);

    /// <summary>Opens the recording player, either for a chosen file or the one passed in.</summary>
    private void OpenRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "SSH-it", "recordings");
            var dialog = new OpenFileDialog
            {
                Title = "Play a recording",
                Filter = "asciinema recordings (*.cast)|*.cast|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }

        try
        {
            new CastPlayerWindow(path!, _store.Settings) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            Log.Warn("play recording: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Could not play the recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- tear-off

    private void TearOffTab_Click(object sender, RoutedEventArgs e) => TearOffTab(_active);

    /// <summary>
    /// Moves a tab's panes into their own window. The TerminalPane objects are handed
    /// over as they are, so the ConPTY process and scrollback survive the move; closing
    /// that window ends the session.
    /// </summary>
    private void TearOffTab(TabEntry? entry)
    {
        if (entry is null) return;

        var panes = entry.Panes.ToList();
        entry.Host.Children.Clear();

        var detached = new DetachedPaneWindow(panes, _store.Settings, entry.Session.Name);
        _app.TrackSurface(detached);
        detached.Show();

        _tabs.Remove(entry);
        TabStrip.Children.Remove(entry.Header);

        if (_active == entry) _active = _tabs.LastOrDefault();
        if (_active is not null) SelectTab(_active);
        else
        {
            TerminalHost.Content = null;
            UpdateEmptyState();
        }

        UpdateBroadcastMenu();
        UpdateStatus();

        SaveTabs();

        StatusMessage.Text = "Tab moved to its own window.";
    }

    // ---------------------------------------------------------------- zmodem

    /// <summary>
    /// ZMODEM send: the local file is chosen here, then the user runs rz on the far end.
    /// Receiving needs no menu - "sz file" is detected on the stream by itself.
    /// </summary>
    private void SendFileZmodem_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Send with ZMODEM (run rz on the far end)",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        _active.Pane.SendFileOverZmodem(dialog.FileName);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.ExitAll();

    /// <summary>Opens an independent main window: own tabs, own placement, shared sessions.</summary>
    private void NewWindow_Click(object sender, RoutedEventArgs e) => _app.CreateWindow(cascadeFrom: this);

    // ---------------------------------------------------------------- edit

    private void Copy_Click(object sender, RoutedEventArgs e) => _active?.Pane.CopySelection();

    private void Paste_Click(object sender, RoutedEventArgs e) => _active?.Pane.PasteClipboard();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _active?.Pane.View.SelectAll();

    private void Find_Click(object sender, RoutedEventArgs e) => _active?.Pane.ShowFind();

    /// <summary>Read-only: the effective configuration and a live handshake, from the user's own ssh.</summary>
    private void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is null)
        {
            StatusMessage.Text = "Select a session to diagnose.";
            return;
        }
        new Views.DiagnoseWindow(ActiveSession) { Owner = this }.ShowDialog();
    }

    /// <summary>Round 3A, R3-05: one read-only pass over every saved SSH host.</summary>
    private void FleetPosture_Click(object sender, RoutedEventArgs e)
        => new Views.FleetPostureWindow(_store.Sessions) { Owner = this }.ShowDialog();

    /// <summary>
    /// R3-22: find SSH hosts on a network the user names. TCP connect only, one port, bounded;
    /// a session exists only after the user adds it. Not a port scanner by construction.
    /// </summary>
    private void NetworkScan_Click(object sender, RoutedEventArgs e)
        => new Views.NetworkScanWindow(_store) { Owner = this }.ShowDialog();

    /// <summary>Round 3B, R3-09: rerun the tab's last command on an interval, highlighting changes.</summary>
    private void WatchCommand_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is null)
        {
            StatusMessage.Text = "Select a session to watch.";
            return;
        }
        new Views.WatchWindow(ActiveSession, _active?.Pane.LastCommand) { Owner = this }.ShowDialog();
    }

    /// <summary>Round 3B, R3-10: run one command on one or two hosts and align the outputs.</summary>
    private void CompareOutput_Click(object sender, RoutedEventArgs e)
        => new Views.DiffWindow(_store.Sessions, ActiveSession, _active?.Pane.LastCommand) { Owner = this }.ShowDialog();

    /// <summary>Round 3B, R3-19: what a destructive command would touch, expanded on the host.</summary>
    private void BlastRadius_Click(object sender, RoutedEventArgs e)
        => new Views.BlastWindow(ActiveSession, _active?.Pane.LastCommand) { Owner = this }.ShowDialog();

    /// <summary>
    /// R3-06: approve one exact command for the agent surface, bound to the active pane's pty so it
    /// ends when that pane does. Opened from the Session menu and from the pane's own menu.
    /// </summary>
    private void ApproveCommand_Click(object sender, RoutedEventArgs e) => ApproveFor(_active?.Pane);

    public void ApproveFor(TerminalPane? pane)
    {
        if (pane is null)
        {
            StatusMessage.Text = "Select a pane to approve a command for.";
            return;
        }
        new Views.ApproveWindow(pane.Session, pane.LastCommand, pane.PtyProcessId, pane.PtyStartedAt)
            { Owner = this }.ShowDialog();
    }

    private void ClearScrollback_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _active.Pane.Buffer.Scrollback.Clear();
        _active.Pane.View.ScrollToBottom();
    }

    // ---------------------------------------------------------------- view

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode) ExitFocusMode();
        var visible = Sidebar.Visibility != Visibility.Visible;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        SidebarColumn.Width = new GridLength(visible ? Math.Max(200, _store.Settings.SidebarWidth) : 0);
        MenuSidebar.IsChecked = visible;
    }

    private void ToggleRightPanel_Click(object sender, RoutedEventArgs e) => ToggleRightPanel();

    private void ToggleStatusBar_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode) ExitFocusMode();
        var visible = StatusBar.Visibility != Visibility.Visible;
        StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MenuStatusBar.IsChecked = visible;
    }

    private bool _focusMode;
    private bool _focusSidebar;
    private bool _focusRightPanel;
    private bool _focusStatusBar;

    /// <summary>
    /// One key that gives the terminal the whole window: the sidebar, the tool panel and the
    /// status bar go away together and come back exactly as they were. The saved layout is not
    /// touched, so a restart opens the panels the user actually chose, never the focus-mode state.
    /// </summary>
    private void ToggleFocusMode_Click(object sender, RoutedEventArgs e) => ToggleFocusMode();

    private void ToggleFocusMode()
    {
        if (_focusMode)
        {
            ExitFocusMode();
            StatusMessage.Text = "Focus mode off.";
            return;
        }

        _focusSidebar = Sidebar.Visibility == Visibility.Visible;
        _focusRightPanel = RightPanel.Visibility == Visibility.Visible;
        _focusStatusBar = StatusBar.Visibility == Visibility.Visible;
        _focusMode = true;
        ApplyChrome(false, false, false);
        MenuFocus.IsChecked = true;
        StatusMessage.Text = "Focus mode: Ctrl+Shift+Z brings the panels back.";
    }

    private void ExitFocusMode()
    {
        if (!_focusMode) return;
        _focusMode = false;
        ApplyChrome(_focusSidebar, _focusRightPanel, _focusStatusBar);
        MenuFocus.IsChecked = false;
    }

    private void ApplyChrome(bool sidebar, bool rightPanel, bool statusBar)
    {
        Sidebar.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        SidebarColumn.Width = new GridLength(sidebar ? Math.Max(200, _store.Settings.SidebarWidth) : 0);
        MenuSidebar.IsChecked = sidebar;

        RightPanel.Visibility = rightPanel ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = RightPanel.Visibility;
        RightPanelColumn.Width = new GridLength(rightPanel ? Math.Max(280, _store.Settings.RightPanelWidth) : 0);
        MenuRightPanel.IsChecked = rightPanel;

        StatusBar.Visibility = statusBar ? Visibility.Visible : Visibility.Collapsed;
        MenuStatusBar.IsChecked = statusBar;
    }

    private void SetTheme(string mode)
    {
        _store.Settings.Theme = mode;
        _store.SaveSettings();
        _app.Theme.Apply(mode);
        RefreshThemeChecks();
    }

    /// <summary>
    /// One entry per theme, built from the catalog so adding a palette does not touch XAML.
    /// Items after the three system choices are the palettes, then high contrast.
    /// </summary>
    private void BuildThemeMenu()
    {
        MenuTheme.Items.Clear();
        foreach (var (id, label) in ThemeCatalog.MenuEntries)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, Tag = id };
            item.Click += (_, _) => SetTheme(id);
            MenuTheme.Items.Add(item);
        }

        if (MenuTheme.Items.Count > 3) MenuTheme.Items.Insert(3, new Separator());
        RefreshThemeChecks();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-0.1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ZoomReset();

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        _fullScreen = !_fullScreen;
        var grid = (Grid)RootBorder.Child;
        grid.RowDefinitions[1].Height = _fullScreen ? new GridLength(0) : new GridLength(30);
        grid.RowDefinitions[2].Height = _fullScreen ? new GridLength(0) : new GridLength(46);
        grid.RowDefinitions[4].Height = _fullScreen ? new GridLength(0) : new GridLength(26);
        MainMenu.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;

        if (_fullScreen)
        {
            WindowState = WindowState.Maximized;
        }
    }

    // ---------------------------------------------------------------- session

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSession is not null) OpenSession(_selectedSession);
        else QuickConnect_Click(sender, e);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => _active?.Pane.Disconnect();

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _active.Pane.Connect(CredentialVault.GetSessionSecret(_active.Session.Id));
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        var session = ActiveSession;
        if (session is not null) OpenSession(session, forceNewTab: true);
        else QuickConnect_Click(sender, e);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) CloseTab(_active);
    }

    private void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tab in _tabs.ToList()) CloseTab(tab);
    }

    private void EditSession_Click(object sender, RoutedEventArgs e) => EditSession(_selectedSession ?? ActiveSession);

    private void EditSession(Session? session)
    {
        if (session is null) return;

        var clone = session.Clone();
        var editor = new SessionEditorWindow(clone, _store, isNew: false) { Owner = this };
        if (editor.ShowDialog() != true) return;

        CopyInto(editor.Session, session);
        _store.SaveSessions();
        RebuildTree();
        RefreshTabTitles();
        UpdateStatus();
    }

    private static void CopyInto(Session from, Session to)
    {
        // The model owns what "edited" means: every writable property except identity and live state.
        // The old hand-kept list here had already fallen behind the model (colour tag, kind, serial,
        // X11 and agent-allow were all dropped on save).
        to.CopyEditableFrom(from);
    }

    private void RefreshTabTitles()
    {
        foreach (var tab in _tabs)
        {
            tab.TitleText.Text = tab.Session.Name;
            UpdateTabHeader(tab.Pane);
        }
        UpdateTitleText();
    }

    private void DuplicateSession_Click(object sender, RoutedEventArgs e) => DuplicateSession(_selectedSession ?? ActiveSession);

    private void DuplicateSession(Session? session)
    {
        if (session is null) return;
        var copy = session.Clone();
        copy.Id = Guid.NewGuid().ToString("n");
        copy.Name = session.Name + " copy";
        _store.Sessions.Add(copy);
        _store.SaveSessions();
        RebuildTree();
        SelectSessionInTree(copy);
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e) => DeleteSession(_selectedSession);

    private void DeleteSession(Session? session)
    {
        if (session is null) return;
        var result = MessageBox.Show(this,
            $"Delete the saved session \"{session.Name}\"?\n\nThis does not affect the remote host.",
            "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        var open = _tabs.FirstOrDefault(t => t.Session.Id == session.Id);
        if (open is not null) CloseTab(open);

        CredentialVault.DeleteSessionSecret(session.Id);
        _store.Sessions.Remove(session);
        _store.SaveSessions();
        _selectedSession = null;
        RebuildTree();
        UpdateEmptyState();
    }

    private void CopySshCommand(Session session)
    {
        var args = SshLauncher.BuildArguments(session);
        var full = $"ssh {args}";
        try
        {
            Clipboard.SetText(full);
            StatusMessage.Text = "ssh command copied to the clipboard.";
        }
        catch (Exception ex) { StatusMessage.Text = ex.Message; }
    }

    private void MoveSessionToFolder(Session session)
    {
        var options = new List<string> { "(no folder)" };
        options.AddRange(_store.Folders.Select(f => f.Name));
        var index = PromptDialog.Choose(this, "Move to folder", "Folder:", options,
            session.FolderId is null ? 0 : options.FindIndex(o => o == _store.Folders.First(f => f.Id == session.FolderId).Name));
        if (index < 0) return;

        session.FolderId = index == 0 ? null : _store.Folders[index - 1].Id;
        _store.SaveSessions();
        RebuildTree();
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => NewFolder(null);

    private void NewFolder(string? parentId)
    {
        var name = PromptDialog.Show(this, "New folder", "Folder name:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;

        _store.Folders.Add(new SessionFolder { Name = name.Trim(), ParentId = parentId });
        _store.SaveFolders();
        RebuildTree();
    }

    private void RenameFolder(SessionFolder folder)
    {
        var name = PromptDialog.Show(this, "Rename folder", "Folder name:", folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        folder.Name = name.Trim();
        _store.SaveFolders();
        RebuildTree();
    }

    private void DeleteFolder(SessionFolder folder)
    {
        var result = MessageBox.Show(this,
            $"Delete the folder \"{folder.Name}\"?\n\nSessions inside it are kept and move to the top level.",
            "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        foreach (var session in _store.Sessions.Where(s => s.FolderId == folder.Id))
            session.FolderId = null;
        foreach (var child in _store.Folders.Where(f => f.ParentId == folder.Id))
            child.ParentId = null;

        _store.Folders.Remove(folder);
        _store.SaveFolders();
        _store.SaveSessions();
        RebuildTree();
    }

    private void SelectSessionInTree(Session session)
    {
        foreach (var item in EnumerateTreeItems(SessionTree.Items))
        {
            if (item.Tag is Session s && s.Id == session.Id)
            {
                item.IsSelected = true;
                item.BringIntoView();
                return;
            }
        }
    }

    private static IEnumerable<TreeViewItem> EnumerateTreeItems(ItemCollection items)
    {
        foreach (var entry in items)
        {
            if (entry is not TreeViewItem item) continue;
            yield return item;
            foreach (var child in EnumerateTreeItems(item.Items)) yield return child;
        }
    }

    // ---------------------------------------------------------------- tools

    private void ShowFiles_Click(object sender, RoutedEventArgs e) => ShowTool(ToolKind.Files);
    private void ShowTunnels_Click(object sender, RoutedEventArgs e) => ShowTool(ToolKind.Tunnels);
    private void ShowInfo_Click(object sender, RoutedEventArgs e) => ShowTool(ToolKind.Info);
    private void ShowSnippets_Click(object sender, RoutedEventArgs e) =>
        new SnippetWindow(_store, _active?.Panes, _active?.Pane) { Owner = this }.ShowDialog();

    /// <summary>L9: a non-modal instrument, so the user can type while it measures.</summary>
    private void ShowPerformance_Click(object sender, RoutedEventArgs e)
    {
        var window = new PerformanceWindow { Owner = this };
        window.Show();
    }

    private void LockSession_Click(object sender, RoutedEventArgs e) => _active?.Pane.OpenLockDialog();

    private void KeyManager_Click(object sender, RoutedEventArgs e) => new KeyManagerWindow { Owner = this }.ShowDialog();

    private void ShellIntegration_Click(object sender, RoutedEventArgs e)
    {
        // Bash, zsh and fish each get the OSC 133 contract through their own hook; the window
        // picks one so nobody has to translate a bash snippet into zsh by hand.
        new ShellIntegrationWindow(_active?.Pane) { Owner = this }.ShowDialog();
    }

    private void Options_Click(object sender, RoutedEventArgs e) => OpenOptions();

    /// <summary>
    /// Opens Options and, when it is accepted, re-applies the shared settings to every window -
    /// a font or palette changed here must not leave the other window on the old one.
    /// </summary>
    internal void OpenOptions()
    {
        var window = new SettingsWindow(_store) { Owner = this };
        if (window.ShowDialog() != true) return;

        _app.ReloadWindowSettings();
        StatusMessage.Text = "Options saved.";
    }

    // ---------------------------------------------------------------- help

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => new ShortcutsWindow { Owner = this }.ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        new TextWindow("What's new in SSH-it", Changelog.Text,
            "This build, in the app. Nothing is fetched: there is no update check and no telemetry.")
        { Owner = this }.ShowDialog();

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Diagnostics.Summary(_store.Sessions.Count, _tabs.Count, _store.Settings.Theme));
            StatusMessage.Text = "Diagnostics copied — version, OS and counts only; no host names or commands.";
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "The clipboard refused the diagnostics: " + ex.Message;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.Directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "Could not open the log folder: " + ex.Message;
        }
    }
}
