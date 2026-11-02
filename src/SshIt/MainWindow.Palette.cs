using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;
using SshIt.Views;

namespace SshIt;

/// <summary>
/// R3-23: the command palette. One Ctrl+Shift+P over the sessions, the command ledger, the snippets,
/// the services and tunnels, the undo points, the menu actions and the settings toggles.
///
/// Two rules the palette keeps:
///   * It types, it does not press Enter. A snippet or a history row is inserted at the prompt with
///     the caret waiting, so Enter is the user's decision — a palette that submits turns one stray
///     keystroke into a command, and these rows are exactly the destructive ones.
///   * A provider that fails to load (a corrupt store, an unreadable chain) drops out of the list
///     instead of taking the palette down: opening the palette must never be the thing that crashes.
/// </summary>
public partial class MainWindow
{
    private void OpenPalette(string? query = null)
    {
        try
        {
            var palette = new PaletteWindow(BuildPaletteItems, RunPaletteItem) { Owner = this };
            if (!string.IsNullOrEmpty(query)) palette.Preload(query);
            palette.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "The command palette could not open: " + ex.Message;
        }
    }

    private void CommandPalette_Click(object sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>Every row, in the order the palette shows them when the query is empty.</summary>
    private IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();
        void Add(Func<IEnumerable<PaletteItem>> source)
        {
            try
            {
                var rows = source();
                if (rows is not null) items.AddRange(rows);
            }
            catch
            {
                // A broken store is a missing provider, not a broken palette.
            }
        }

        Add(() => CommandPalette.SessionItems(_store.Sessions));
        Add(CommandPalette.ActionItems);
        Add(() => CommandPalette.SnippetItems(_store.Snippets));
        Add(() =>
        {
            // C9: the ledger knows the output; the evidence chain only knows the command. Use the
            // ledger when it has rows, and fall back so a machine that upgraded keeps its history.
            var rows = CommandLedger.Tail(200);
            return rows.Count > 0
                ? CommandPalette.LedgerItems(rows)
                : CommandPalette.HistoryItems(EvidenceChain.Tail(1200));
        });
        Add(() => CommandPalette.WorkspaceItems(WorkspaceStore.Load()));
        Add(() => CommandPalette.ServiceItems(ServiceTunnels.Load()));
        Add(() => CommandPalette.TunnelItems(TunnelSupervisor.List()));
        Add(() => CommandPalette.SnapshotItems(UndoStore.List()));
        Add(() => CommandPalette.SettingItems(_store.Settings.StatusBarVisible, _store.Settings.SidebarVisible,
            _store.Settings.RightPanelVisible, _pinned, _active?.Broadcast ?? false,
            _active is { Panes.Count: > 1 }));
        Add(() => CommandPalette.ThemeItems(ThemeCatalog.MenuEntries, _store.Settings.Theme));
        return items;
    }

    private void RunPaletteItem(PaletteItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case "session":
                    OpenSessionById(item.Payload);
                    break;

                case "snippet":
                    InsertPaletteSnippet(item.Payload);
                    break;

                case "history":
                    InsertPaletteCommand(item.Payload);
                    break;

                case "service":
                    BringUpPaletteService(item.Payload);
                    break;

                case "tunnel":
                    ShowTool(ToolKind.Tunnels);
                    StatusMessage.Text = "Port forwarding is open — the tunnel itself is managed there.";
                    break;

                case "snapshot":
                    CopyPaletteRestore(item.Payload);
                    break;

                case "workspace":
                {
                    var workspace = WorkspaceStore.Find(item.Payload);
                    if (workspace is null) { StatusMessage.Text = "That workspace no longer exists."; break; }
                    ApplyWorkspace(workspace);
                    break;
                }

                case "setting":
                    ApplyPaletteSetting(item.Payload);
                    break;

                default:
                    RunPaletteAction(item.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "The palette could not run that: " + ex.Message;
        }
    }

    private bool PaletteHasTab(string what)
    {
        if (_active is not null) return true;
        StatusMessage.Text = what + " needs an open tab — connect a session first.";
        return false;
    }

    private void InsertPaletteSnippet(string id)
    {
        if (!PaletteHasTab("A snippet")) return;

        var snippet = _store.Snippets.FirstOrDefault(s => s.Id == id);
        if (snippet is null)
        {
            StatusMessage.Text = "That snippet no longer exists.";
            return;
        }

        var text = SnippetText.Expand(snippet.Command, _active!.Session, _active.Pane.WorkingDirectory);
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage.Text = "That snippet is empty.";
            return;
        }

        _active.Pane.SendInput(text, submit: false);
        StatusMessage.Text = "Inserted \"" + snippet.Name + "\" at the prompt — press Enter to run it.";
    }

    private void InsertPaletteCommand(string command)
    {
        if (!PaletteHasTab("A command")) return;
        if (string.IsNullOrWhiteSpace(command)) return;

        _active!.Pane.SendInput(command, submit: false);
        StatusMessage.Text = "Inserted the command at the prompt — press Enter to run it.";
    }

    private void BringUpPaletteService(string name)
    {
        ServiceDefinition? service;
        try
        {
            service = ServiceTunnels.Load().FirstOrDefault(s => s.Name == name);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "The service store could not be read: " + ex.Message;
            return;
        }

        if (service is null)
        {
            StatusMessage.Text = "That service no longer exists.";
            return;
        }

        var session = _store.Sessions.FirstOrDefault(s => s.Id == service.SessionId)
                      ?? _store.Sessions.FirstOrDefault(s =>
                          string.Equals(s.Name, service.SessionId, StringComparison.OrdinalIgnoreCase));

        if (session is null)
        {
            StatusMessage.Text = "Service \"" + service.Name + "\" names a session that no longer exists.";
            return;
        }

        StatusMessage.Text = "Bringing up service " + service.Name + "…";
        var target = service;
        Task.Run(() => ServiceTunnels.Up(target, session)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                var message = task.Exception?.GetBaseException().Message ?? "unknown error";
                Dispatcher.BeginInvoke(new Action(() =>
                    StatusMessage.Text = "Service " + target.Name + " failed: " + message));
                return;
            }

            var report = task.Result;
            Dispatcher.BeginInvoke(new Action(() =>
                StatusMessage.Text = "Service " + report.Name + ": " + report.Status
                                     + (report.Detail.Length > 0 ? " — " + report.Detail : "")));
        }, TaskScheduler.Default);
    }

    private void CopyPaletteRestore(string restore)
    {
        if (string.IsNullOrWhiteSpace(restore))
        {
            StatusMessage.Text = "That undo point carries no restore command.";
            return;
        }

        try
        {
            Clipboard.SetText(restore);
            StatusMessage.Text = "Restore command copied — paste it into the pane you want to roll back.";
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "The clipboard refused the restore command: " + ex.Message;
        }
    }

    private void ApplyPaletteSetting(string payload)
    {
        if (payload.StartsWith("theme:", StringComparison.Ordinal))
        {
            var id = payload[6..];
            SetTheme(id);
            StatusMessage.Text = "Theme: " + id + ".";
            return;
        }

        switch (payload)
        {
            case "set:statusbar":
                ToggleStatusBar_Click(this, new RoutedEventArgs());
                StatusMessage.Text = "Status bar " + (StatusBar.Visibility == Visibility.Visible ? "on" : "off") + ".";
                break;

            case "set:sidebar":
                ToggleSidebar_Click(this, new RoutedEventArgs());
                StatusMessage.Text = "Session sidebar "
                                     + (Sidebar.Visibility == Visibility.Visible ? "on" : "off") + ".";
                break;

            case "set:rightpanel":
                ToggleRightPanel();
                StatusMessage.Text = "Tools panel " + (RightPanel.Visibility == Visibility.Visible ? "on" : "off") + ".";
                break;

            case "set:pin":
                SetPinned(!_pinned);
                break;

            case "set:broadcast":
                if (!PaletteHasTab("Broadcast")) return;
                SetBroadcast(_active, !(_active?.Broadcast ?? false));
                break;

            default:
                StatusMessage.Text = "Unknown palette setting: " + payload;
                break;
        }
    }

    /// <summary>The menu, reached by name. Every id here matches one in CommandPalette.Actions.</summary>
    private void RunPaletteAction(string id)
    {
        switch (id)
        {
            // File
            case "new-session": NewSession(null); break;
            case "quick-connect": QuickConnect_Click(this, new RoutedEventArgs()); break;
            case "local-terminal": LocalTerminal_Click(this, new RoutedEventArgs()); break;
            case "new-window": NewWindow_Click(this, new RoutedEventArgs()); break;
            case "import-ssh": ImportSshConfig_Click(this, new RoutedEventArgs()); break;
            case "import-putty": ImportPutty_Click(this, new RoutedEventArgs()); break;
            case "export-sessions": ExportSessions_Click(this, new RoutedEventArgs()); break;
            case "import-sessions": ImportSessions_Click(this, new RoutedEventArgs()); break;
            case "save-workspace": SaveWorkspaceInteractive(); break;
            case "open-data": OpenDataFolder_Click(this, new RoutedEventArgs()); break;
            case "exit": Exit_Click(this, new RoutedEventArgs()); break;

            // Edit
            case "copy": if (PaletteHasTab("Copy")) Copy_Click(this, new RoutedEventArgs()); break;
            case "paste": if (PaletteHasTab("Paste")) Paste_Click(this, new RoutedEventArgs()); break;
            case "select-all": if (PaletteHasTab("Select all")) SelectAll_Click(this, new RoutedEventArgs()); break;
            case "find": if (PaletteHasTab("Find")) Find_Click(this, new RoutedEventArgs()); break;
            case "clear-scrollback":
                if (!PaletteHasTab("Clear scrollback")) return;
                ClearScrollback_Click(this, new RoutedEventArgs());
                StatusMessage.Text = "Scrollback cleared.";
                break;

            // View
            case "zoom-in": Zoom(0.1); StatusMessage.Text = "Zoom " + _zoom.ToString("0.0") + "×."; break;
            case "zoom-out": Zoom(-0.1); StatusMessage.Text = "Zoom " + _zoom.ToString("0.0") + "×."; break;
            case "zoom-reset": ZoomReset(); StatusMessage.Text = "Zoom reset."; break;
            case "reset-opacity": ResetOpacity_Click(this, new RoutedEventArgs()); break;
            case "fullscreen": FullScreen_Click(this, new RoutedEventArgs()); break;
            case "split-right":
                if (PaletteHasTab("Split")) SplitPane(vertical: true);
                break;
            case "split-down":
                if (PaletteHasTab("Split")) SplitPane(vertical: false);
                break;
            case "close-pane":
                if (PaletteHasTab("Close pane")) ClosePane_Click(this, new RoutedEventArgs());
                break;
            case "send-to-all":
                SendToAllInteractive();
                break;
            case "focus-mode":
                ToggleFocusMode();
                break;

            // Session
            case "connect": if (PaletteHasTab("Connect")) Connect_Click(this, new RoutedEventArgs()); break;
            case "disconnect": if (PaletteHasTab("Disconnect")) Disconnect_Click(this, new RoutedEventArgs()); break;
            case "reconnect": if (PaletteHasTab("Reconnect")) Reconnect_Click(this, new RoutedEventArgs()); break;
            case "diagnose": if (PaletteHasTab("Diagnose")) Diagnose_Click(this, new RoutedEventArgs()); break;
            case "posture": FleetPosture_Click(this, new RoutedEventArgs()); break;
            case "scan": NetworkScan_Click(this, new RoutedEventArgs()); break;
            case "watch": WatchCommand_Click(this, new RoutedEventArgs()); break;
            case "diff": CompareOutput_Click(this, new RoutedEventArgs()); break;
            case "blast": BlastRadius_Click(this, new RoutedEventArgs()); break;
            case "approve": ApproveCommand_Click(this, new RoutedEventArgs()); break;
            case "new-tab": NewTab_Click(this, new RoutedEventArgs()); break;
            case "close-tab": CloseTab_Click(this, new RoutedEventArgs()); break;
            case "close-all-tabs": CloseAllTabs_Click(this, new RoutedEventArgs()); break;
            case "tear-off": TearOffTab_Click(this, new RoutedEventArgs()); break;
            case "send-zmodem": SendFileZmodem_Click(this, new RoutedEventArgs()); break;
            case "edit-session": EditSession_Click(this, new RoutedEventArgs()); break;
            case "duplicate-session": DuplicateSession_Click(this, new RoutedEventArgs()); break;
            case "delete-session": DeleteSession_Click(this, new RoutedEventArgs()); break;

            // Tools
            case "files": ShowFiles_Click(this, new RoutedEventArgs()); break;
            case "tunnels": ShowTunnels_Click(this, new RoutedEventArgs()); break;
            case "snippets": ShowSnippets_Click(this, new RoutedEventArgs()); break;
            case "lock": if (PaletteHasTab("Lock")) LockSession_Click(this, new RoutedEventArgs()); break;
            case "info": ShowInfo_Click(this, new RoutedEventArgs()); break;
            case "performance": ShowPerformance_Click(this, new RoutedEventArgs()); break;
            case "key-manager": KeyManager_Click(this, new RoutedEventArgs()); break;
            case "shell-integration": ShellIntegration_Click(this, new RoutedEventArgs()); break;
            case "play-recording": PlayRecording_Click(this, new RoutedEventArgs()); break;
            case "options": Options_Click(this, new RoutedEventArgs()); break;

            // Help
            case "shortcuts": Shortcuts_Click(this, new RoutedEventArgs()); break;
            case "about": About_Click(this, new RoutedEventArgs()); break;

            default:
                StatusMessage.Text = "That palette action is not wired: " + id;
                break;
        }
    }
}
