using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;
using Microsoft.Win32;

namespace SshIt.Views;

/// <summary>Remote file browser over the OpenSSH sftp subsystem.</summary>
public sealed class SftpPanel : UserControl
{
    private readonly MainWindow _owner;
    private readonly DataStore _store;
    private readonly ListView _list = new();
    private readonly TextBox _path = new();
    private readonly TextBlock _status = new();
    private readonly ProgressBar _progress = new() { Height = 3, Visibility = Visibility.Collapsed };
    private readonly Button _up = new(), _refresh = new();

    private SftpClient? _client;
    private Session? _session;
    private bool _sessionConnected = true;
    private string _cwd = ".";
    private bool _loadingDir;
    private int _generation;
    private int _connectingGeneration = -1;
    private string? _followPath;
    private readonly CheckBox _followBox = new();
    private readonly Button _uploadButton = new();
    private readonly Button _deployButton = new();
    private CancellationTokenSource? _transfer;
    private readonly List<FileSystemWatcher> _watchers = new();

    // The local half of the panel. Same list shape as the remote one so a row reads the same
    // on either side, and a folder can be the target of a download or a mirror.
    private readonly ListView _localList = new();
    private readonly TextBox _localPath = new();
    private readonly Button _localUp = new(), _localRefresh = new(), _localSend = new();
    private readonly Button _mirrorButton = new(), _downloadButton = new();
    private readonly CheckBox _resumeBox = new();
    private string _localCwd = "";

    public SftpPanel(MainWindow owner, DataStore store, Session? session)
    {
        _owner = owner;
        _store = store;
        // Dynamic resource: a live theme switch must repaint this surface, and a brush captured with
        // FindResource at construction keeps the old palette until the panel is rebuilt (D6).
        SetResourceReference(BackgroundProperty, "Brush.Panel");
        AllowDrop = true;

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionMode = SelectionMode.Extended;
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Name", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        view.Columns.Add(new GridViewColumn { Header = "Size", Width = 62, DisplayMemberBinding = new System.Windows.Data.Binding("SizeText") });
        view.Columns.Add(new GridViewColumn { Header = "Modified", Width = 96, DisplayMemberBinding = new System.Windows.Data.Binding("ModifiedText") });
        view.Columns.Add(new GridViewColumn { Header = "Mode", Width = 62, DisplayMemberBinding = new System.Windows.Data.Binding("PermissionsText") });
        _list.View = view;
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.Key == Key.Back) { NavigateUp(); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
            else if (e.Key == Key.F5) { Reload(); e.Handled = true; }
        };
        _list.ContextMenu = BuildMenu();
        _list.SelectionChanged += (_, _) => UpdateDeployEnabled();

        // A narrow dock makes a side-by-side dual pane useless (two 120 px lists), so the
        // local half sits under the remote one with a draggable splitter between them.
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 2) };

        _up.Content = Glyph("\uE74A", "Up one level");
        _up.Width = 28; _up.Height = 26; _up.Style = (Style)Application.Current.FindResource("Btn.Icon");
        _up.Margin = new Thickness(0, 0, 4, 4);
        _up.Click += (_, _) => NavigateUp();

        _refresh.Content = Glyph("\uE72C", "Refresh");
        _refresh.Width = 28; _refresh.Height = 26; _refresh.Style = (Style)Application.Current.FindResource("Btn.Icon");
        _refresh.Margin = new Thickness(0, 0, 4, 4);
        _refresh.Click += (_, _) => Reload();

        _downloadButton.Content = "Download";
        _downloadButton.ToolTip = "Download the selected files into the local folder below.";
        _downloadButton.Height = 26;
        _downloadButton.Margin = new Thickness(0, 0, 4, 4);
        _downloadButton.Padding = new Thickness(10, 0, 10, 0);
        _downloadButton.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _downloadButton.IsEnabled = false;
        _downloadButton.Click += (_, _) => DownloadSelected(LocalTarget());

        _uploadButton.Content = "Upload";
        _uploadButton.Height = 26;
        _uploadButton.Margin = new Thickness(0, 0, 4, 4);
        _uploadButton.Padding = new Thickness(10, 0, 10, 0);
        _uploadButton.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _uploadButton.Click += (_, _) => Upload();

        _deployButton.Content = "Deploy…";
        _deployButton.ToolTip = "Upload the selected local file beside the remote target, validate it, then swap it into place with the old file kept.";
        _deployButton.Height = 26;
        _deployButton.Margin = new Thickness(0, 0, 4, 4);
        _deployButton.Padding = new Thickness(10, 0, 10, 0);
        _deployButton.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _deployButton.IsEnabled = false;
        _deployButton.Click += (_, _) => Deploy();
        AutomationProperties.SetAutomationId(_deployButton, "SftpDeploy");

        var folder = new Button
        {
            Content = Glyph("\uE8F4", "New folder in the remote directory"),
            Width = 28,
            Height = 26,
            Margin = new Thickness(0, 0, 4, 4),
            Style = (Style)Application.Current.FindResource("Btn.Icon")
        };
        folder.Click += (_, _) => NewFolder();

        _mirrorButton.Content = "Mirror";
        _mirrorButton.ToolTip = "Make the local folder match the remote one. Same-size files are skipped.";
        _mirrorButton.Height = 26;
        _mirrorButton.Margin = new Thickness(0, 0, 4, 4);
        _mirrorButton.Padding = new Thickness(10, 0, 10, 0);
        _mirrorButton.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _mirrorButton.IsEnabled = false;
        _mirrorButton.Click += (_, _) => Mirror();

        _resumeBox.Content = "Resume";
        _resumeBox.ToolTip = "Continue a partial download where it stopped instead of starting again.";
        _resumeBox.FontSize = 11;
        _resumeBox.VerticalAlignment = VerticalAlignment.Center;
        _resumeBox.Margin = new Thickness(4, 0, 0, 4);
        _resumeBox.IsChecked = _store.Settings.SftpResume;
        _resumeBox.Checked += (_, _) => SetResume(true);
        _resumeBox.Unchecked += (_, _) => SetResume(false);

        _followBox.Content = "Follow shell";
        _followBox.ToolTip = "Keep this listing on the directory the shell reports (OSC 7).";
        _followBox.FontSize = 11;
        _followBox.VerticalAlignment = VerticalAlignment.Center;
        _followBox.Margin = new Thickness(10, 0, 0, 4);
        _followBox.IsChecked = _store.Settings.SftpFollowShell;
        _followBox.Checked += (_, _) => SetFollow(true);
        _followBox.Unchecked += (_, _) => SetFollow(false);

        toolbar.Children.Add(_up);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_downloadButton);
        toolbar.Children.Add(_uploadButton);
        toolbar.Children.Add(_deployButton);
        toolbar.Children.Add(folder);
        toolbar.Children.Add(_mirrorButton);
        toolbar.Children.Add(_resumeBox);
        toolbar.Children.Add(_followBox);

        _path.Height = 26;
        _path.Margin = new Thickness(10, 0, 10, 6);
        _path.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Navigate(_path.Text.Trim());
        };

        _status.Margin = new Thickness(12, 4, 12, 8);
        _status.FontSize = 11;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");

        // ---- the local half ----
        _localList.Background = Brushes.Transparent;
        _localList.BorderThickness = new Thickness(0);
        _localList.SelectionMode = SelectionMode.Extended;
        var localView = new GridView();
        localView.Columns.Add(new GridViewColumn { Header = "Name", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        localView.Columns.Add(new GridViewColumn { Header = "Size", Width = 62, DisplayMemberBinding = new System.Windows.Data.Binding("SizeText") });
        localView.Columns.Add(new GridViewColumn { Header = "Modified", Width = 96, DisplayMemberBinding = new System.Windows.Data.Binding("ModifiedText") });
        _localList.View = localView;
        _localList.MouseDoubleClick += (_, _) => OpenLocalSelected();
        _localList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenLocalSelected(); e.Handled = true; }
            else if (e.Key == Key.Back) { LocalUp(); e.Handled = true; }
            else if (e.Key == Key.F5) { LoadLocal(_localCwd); e.Handled = true; }
        };
        _localList.ContextMenu = BuildLocalMenu();
        _localList.SelectionChanged += (_, _) => UpdateDeployEnabled();
        _localList.AllowDrop = true;

        _localUp.Content = Glyph("\uE74A", "Up one local level");
        _localUp.Width = 26; _localUp.Height = 24; _localUp.Style = (Style)Application.Current.FindResource("Btn.Icon");
        _localUp.Margin = new Thickness(0, 0, 4, 0);
        _localUp.Click += (_, _) => LocalUp();

        _localRefresh.Content = Glyph("\uE72C", "Refresh the local folder");
        _localRefresh.Width = 26; _localRefresh.Height = 24; _localRefresh.Style = (Style)Application.Current.FindResource("Btn.Icon");
        _localRefresh.Margin = new Thickness(0, 0, 4, 0);
        _localRefresh.Click += (_, _) => LoadLocal(_localCwd);

        _localSend.Content = Glyph("\uE898", "Upload the selected files to the remote folder");
        _localSend.Width = 26; _localSend.Height = 24; _localSend.Style = (Style)Application.Current.FindResource("Btn.Icon");
        _localSend.Margin = new Thickness(4, 0, 0, 0);
        _localSend.Click += (_, _) => UploadLocalSelection();

        var localHeader = new DockPanel { Margin = new Thickness(8, 2, 8, 4), LastChildFill = true };
        DockPanel.SetDock(_localUp, Dock.Left);
        DockPanel.SetDock(_localRefresh, Dock.Left);
        DockPanel.SetDock(_localSend, Dock.Right);
        localHeader.Children.Add(_localUp);
        localHeader.Children.Add(_localRefresh);
        localHeader.Children.Add(_localSend);
        _localPath.Height = 24;
        _localPath.ToolTip = "Local folder — the target for Download and Mirror.";
        _localPath.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            NavigateLocal(_localPath.Text.Trim());
        };
        localHeader.Children.Add(_localPath);

        var localGrid = new Grid();
        localGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        localGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(localHeader, 0);
        Grid.SetRow(_localList, 1);
        localGrid.Children.Add(localHeader);
        localGrid.Children.Add(_localList);

        var splitter = new GridSplitter
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        splitter.SetResourceReference(BackgroundProperty, "Brush.Border");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_path, 1);
        Grid.SetRow(_list, 2);
        Grid.SetRow(splitter, 3);
        Grid.SetRow(localGrid, 4);
        Grid.SetRow(_progress, 5);
        Grid.SetRow(_status, 6);
        grid.Children.Add(toolbar);
        grid.Children.Add(_path);
        grid.Children.Add(_list);
        grid.Children.Add(splitter);
        grid.Children.Add(localGrid);
        grid.Children.Add(_progress);
        grid.Children.Add(_status);
        Content = grid;

        Unloaded += (_, _) =>
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            // Every Files/Tunnels/Files round trip builds a new panel; without this its
            // ssh.exe outlives the panel for the life of the app. SetSession reconnects if
            // this instance is ever shown again.
            _client?.Dispose();
            _client = null;
        };

        Drop += OnDrop;
        _list.Drop += OnDrop;
        _list.AllowDrop = true;

        _localCwd = _store.Settings.SftpLocalPath;
        if (string.IsNullOrEmpty(_localCwd) || !Directory.Exists(_localCwd)) _localCwd = LocalFiles.DefaultPath();
        LoadLocal(_localCwd);

        SetSession(session);
    }

    private static TextBlock Glyph(string text, string tooltip) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
        FontSize = 11,
        ToolTip = tooltip
    };

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item("Open", "Enter", OpenSelected));
        menu.Items.Add(Item("Edit remote file…", "", () => _ = EditRemoteAsync()));
        menu.Items.Add(Item("Download to local folder", "", () => DownloadSelected(LocalTarget())));
        menu.Items.Add(Item("Download…", "", () => DownloadSelected()));
        menu.Items.Add(Item("Upload…", "", Upload));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("New folder…", "", NewFolder));
        menu.Items.Add(Item("Rename…", "F2", RenameSelected));
        menu.Items.Add(Item("Delete", "Del", DeleteSelected));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy path", "", () =>
        {
            if (_list.SelectedItem is SftpEntry entry) Clipboard.SetText(entry.FullPath);
        }));
        menu.Items.Add(Item("Refresh", "F5", Reload));
        return menu;
    }

    public void SetSession(Session? session, bool connected = true)
    {
        // A lazily restored tab has no child, so there is nothing to authenticate as: dialling sftp
        // here would produce background traffic for a session the user has not opened yet.
        if (_session?.Id == session?.Id && _sessionConnected == connected &&
            (!connected || _client is { IsConnected: true } || _connectingGeneration == _generation)) return;

        // A tab switch or a restore can land while a connect is still in flight. Bump the
        // generation so the older attempt disposes its client instead of rebinding this panel
        // to a session that is no longer active (the old guard silently dropped the new one).
        _generation++;
        _transfer?.Cancel();
        _client?.Dispose();
        _client = null;
        _session = session;
        _sessionConnected = connected;
        _cwd = ".";
        _list.Items.Clear();
        _downloadButton.IsEnabled = false;
        _mirrorButton.IsEnabled = false;
        _deployButton.IsEnabled = false;

        if (session is null)
        {
            _status.Text = "Select a session to browse its files.";
            return;
        }

        if (session.IsLocal)
        {
            _status.Text = "Local shell — file browsing applies to SSH sessions.";
            return;
        }

        if (!connected)
        {
            _status.Text = "Not connected — press Enter or click Connect in the pane, then browse here.";
            return;
        }

        Connect(session, _generation);
    }

    /// <summary>
    /// Points the listing at the directory the shell reported (OSC 7). Called on every prompt,
    /// so it only acts when the panel is connected, following, and actually somewhere else.
    /// </summary>
    public void FollowShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _followPath = path;
        if (_followBox.IsChecked != true || _client is null) return;
        if (string.Equals(path, _cwd, StringComparison.Ordinal)) return;
        _ = FollowAsync(path);
    }

    private void SetFollow(bool on)
    {
        _store.Settings.SftpFollowShell = on;
        _store.SaveSettings();
        if (on) FollowShell(_followPath);
    }

    private async Task FollowAsync(string path)
    {
        if (_loadingDir) return;
        if (await LoadDirectory(path)) return;

        // The shell can sit somewhere the SFTP user cannot read, or a directory can vanish
        // between the prompt and the listing. Stop chasing that path until the shell moves.
        _followPath = _cwd;
        _status.Text = $"Files: {path} is not readable — showing {_cwd}";
    }

    private async void Connect(Session session, int generation)
    {
        _connectingGeneration = generation;
        _status.Text = "Connecting to " + session.DisplayTarget + "…";
        try
        {
            var client = new SftpClient();
            await client.ConnectAsync(session, CredentialVault.GetSessionSecret(session.Id));
            if (generation != _generation) { client.Dispose(); return; }
            _client = client;
            _downloadButton.IsEnabled = true;
            _mirrorButton.IsEnabled = true;
            UpdateDeployEnabled();

            var start = _followBox.IsChecked == true && !string.IsNullOrEmpty(_followPath)
                ? _followPath!
                : client.HomePath;
            if (!await LoadDirectory(start) && start != client.HomePath)
                await LoadDirectory(client.HomePath);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            _status.Text = "Files unavailable: " + ex.Message;
            Log.Warn("sftp connect: " + ex.Message);
        }
        finally
        {
            if (_connectingGeneration == generation) _connectingGeneration = -1;
        }
    }

    private async Task<bool> LoadDirectory(string path)
    {
        if (_client is null) return false;
        var client = _client;
        var generation = _generation;
        _loadingDir = true;
        _status.Text = "Reading " + path + "…";
        try
        {
            var entries = await client.ListDirectoryAsync(path);
            if (generation != _generation || client != _client) return false;

            _cwd = path;
            _path.Text = path;
            _uploadButton.ToolTip = "Upload into " + path;
            UpdateDeployEnabled();
            _list.Items.Clear();
            foreach (var entry in entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                _list.Items.Add(entry);

            _status.Text = $"{entries.Count} item(s) · {path}";
            return true;
        }
        catch (Exception ex)
        {
            if (generation == _generation) _status.Text = ex.Message;
            return false;
        }
        finally
        {
            if (generation == _generation) _loadingDir = false;
        }
    }

    private void Reload() => _ = LoadDirectory(_cwd);

    private void Navigate(string path)
    {
        if (_client is null) return;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (path.StartsWith("~")) path = _client.HomePath + path[1..];
        _ = LoadDirectory(path);
    }

    private void NavigateUp()
    {
        if (_cwd is "." or "/") return;
        var parent = _cwd.TrimEnd('/');
        var slash = parent.LastIndexOf('/');
        Navigate(slash <= 0 ? "/" : parent[..slash]);
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is not SftpEntry entry) return;
        if (entry.IsDirectory) { Navigate(entry.FullPath); return; }
        // A file goes into the local pane's folder — visible, and the same place Download and
        // Mirror use — instead of a temp directory nobody can find afterwards.
        DownloadSelected(LocalTarget(), openAfter: true);
    }

    /// <summary>
    /// Downloads the selected files. A folder means write them there by name; no folder means
    /// ask per file. Resume appends to a shorter local file instead of starting again.
    /// </summary>
    private async void DownloadSelected(string? folder = null, bool openAfter = false)
    {
        if (_client is null || _session is null) return;
        var client = _client;
        var selected = _list.SelectedItems.OfType<SftpEntry>().Where(e => !e.IsDirectory).ToList();
        if (_list.SelectedItem is SftpEntry single && !single.IsDirectory && selected.Count == 0) selected.Add(single);
        if (selected.Count == 0) return;

        var resume = _resumeBox.IsChecked == true;
        var saved = 0;
        foreach (var entry in selected)
        {
            var local = folder is not null ? Path.Combine(folder, entry.Name) : AskSave(entry.Name);
            if (local is null) continue;

            try
            {
                BeginTransfer("Downloading " + entry.Name);
                await client.DownloadAsync(entry.FullPath, local,
                    (done, total) => Dispatcher.BeginInvoke(new Action(() => SetProgress(done, total))),
                    CancellationToken.None, resume);
                EndTransfer($"Downloaded {entry.Name}");
                saved++;

                if (openAfter)
                {
                    try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); }
                    catch (Exception ex) { _status.Text = ex.Message; }
                }
            }
            catch (Exception ex)
            {
                EndTransfer("Download failed: " + ex.Message);
            }
        }

        if (folder is not null && saved > 0)
        {
            if (string.Equals(folder, _localCwd, StringComparison.OrdinalIgnoreCase)) LoadLocal(_localCwd);
            _status.Text = $"Downloaded {saved} file(s) to {folder}";
        }
    }

    // ---------------------------------------------------------------- local half

    /// <summary>The local folder, or null with the reason on the status line.</summary>
    private string? LocalTarget()
    {
        if (!string.IsNullOrEmpty(_localCwd) && Directory.Exists(_localCwd)) return _localCwd;
        _status.Text = "Pick a local folder first — type a path in the local box.";
        return null;
    }

    private void LoadLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var entries = LocalFiles.List(path);
            _localCwd = path;
            _localPath.Text = path;
            _localSend.ToolTip = "Upload the selected files to " + (_client is null ? "the remote folder" : _cwd);
            _localList.Items.Clear();
            foreach (var entry in entries) _localList.Items.Add(entry);

            if (_store.Settings.SftpLocalPath != path)
            {
                _store.Settings.SftpLocalPath = path;
                try { _store.SaveSettings(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Local: " + ex.Message;
        }
    }

    private void NavigateLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        LoadLocal(path);
    }

    private void LocalUp()
    {
        var parent = LocalFiles.Parent(_localCwd);
        if (parent is null) return;
        LoadLocal(parent);
    }

    private void OpenLocalSelected()
    {
        if (_localList.SelectedItem is not LocalEntry entry) return;
        if (entry.IsDirectory) { LoadLocal(entry.FullPath); return; }
        try { Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    /// <summary>B7: safe deploy needs one local file (the bytes) and something on the remote side to
    /// aim at; the dialog shows the exact target, so a bare listing folder is allowed as the target.</summary>
    private void UpdateDeployEnabled()
    {
        _deployButton.IsEnabled = _client is { IsConnected: true } && _session is not null
            && _localList.SelectedItems.OfType<LocalEntry>().Any(e => !e.IsDirectory);
    }

    private void Deploy()
    {
        if (_client is null || _session is null) { _status.Text = "Deploy needs a connected SSH session."; return; }
        if (_localList.SelectedItem is not LocalEntry source || source.IsDirectory)
        {
            _status.Text = "Select the local file to deploy.";
            return;
        }
        if (_localList.SelectedItems.Count > 1)
        {
            _status.Text = "Deploy takes one file at a time — select a single local file.";
            return;
        }

        var selection = _list.SelectedItem as SftpEntry;
        var target = SafeDeploy.TargetFor(selection?.FullPath, selection?.IsDirectory ?? false, _cwd, source.FullPath);
        if (target is null) { _status.Text = "Select a remote file or folder to deploy into."; return; }

        var dialog = new DeployWindow(_session, _client, source.FullPath, target)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();

        if (dialog.Result is { Ok: true, DryRun: false } result)
        {
            _status.Text = "Deployed " + Path.GetFileName(source.FullPath) + " to " + result.Remote
                           + (result.Replaced ? " (previous kept as " + Path.GetFileName(result.Backup) + ")" : "");
            Reload();
        }
    }

    private async void UploadLocalSelection()
    {
        if (_client is null) { _status.Text = "Upload needs a connected SSH session."; return; }
        var files = _localList.SelectedItems.OfType<LocalEntry>()
            .Where(e => !e.IsDirectory).Select(e => e.FullPath).ToList();
        if (files.Count == 0) { _status.Text = "Select one or more local files to upload."; return; }
        await UploadFiles(files);
    }

    private ContextMenu BuildLocalMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item("Open", "Enter", OpenLocalSelected));
        menu.Items.Add(Item("Upload to remote", "", UploadLocalSelection));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy path", "", () =>
        {
            if (_localList.SelectedItem is LocalEntry entry) Clipboard.SetText(entry.FullPath);
        }));
        menu.Items.Add(Item("Refresh", "F5", () => LoadLocal(_localCwd)));
        return menu;
    }

    private void SetResume(bool on)
    {
        _store.Settings.SftpResume = on;
        try { _store.SaveSettings(); } catch { }
    }

    /// <summary>
    /// Makes the local folder match the remote one. The plan is computed first and shown
    /// before anything is written, so a mistyped target cannot silently pull a tree down.
    /// </summary>
    private async void Mirror()
    {
        if (_client is null || _session is null) { _status.Text = "Mirror needs a connected SSH session."; return; }
        var client = _client;
        var remote = _cwd;
        var local = LocalTarget();
        if (local is null) return;

        try
        {
            BeginTransfer($"Planning {remote} → {local}…", indeterminate: true);
            var plan = await client.MirrorAsync(remote, local, dryRun: true, log: null, CancellationToken.None);
            var summary = SftpClient.SummariseMirror(plan);
            EndTransfer("Mirror plan: " + summary);

            // Only downloads make a mirror worth running: directories always plan as
            // create-dir, so counting them here asked for confirmation on a tree that was
            // already up to date.
            if (!plan.Any(i => i.Action == "download"))
            {
                _status.Text = "Nothing to mirror — " + summary + ".";
                return;
            }

            var answer = MessageBox.Show(Window.GetWindow(this),
                $"Mirror\n\n{remote}\n  → {local}\n\n{summary}\n\n" +
                "Folders are created, a file of the same size is skipped, anything else is downloaded.",
                "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            BeginTransfer($"Mirroring {remote}…", indeterminate: true);
            var seen = 0;
            var done = await client.MirrorAsync(remote, local, dryRun: false,
                log: line => Dispatcher.BeginInvoke(new Action(() =>
                {
                    // One line per entry can be thousands; repaint the status line sparingly.
                    if (++seen % 4 == 0) _status.Text = line;
                })),
                CancellationToken.None);
            EndTransfer("Mirror finished — " + SftpClient.SummariseMirror(done));
            LoadLocal(local);
        }
        catch (Exception ex)
        {
            EndTransfer("Mirror failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- edit in place

    private async Task EditRemoteAsync()
    {
        if (_client is null || _session is null) return;
        if (_list.SelectedItem is not SftpEntry entry || entry.IsDirectory) return;

        var dir = Path.Combine(Path.GetTempPath(), "SSH-it", "edit", _session.Id);
        Directory.CreateDirectory(dir);
        var local = Path.Combine(dir, entry.Name);

        try
        {
            BeginTransfer("Opening " + entry.Name + "…");
            await _client.DownloadAsync(entry.FullPath, local, null, CancellationToken.None);
            EndTransfer("Editing " + entry.Name + " — save the file to upload it");

            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });

            var watcher = new FileSystemWatcher(dir, entry.Name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
            debounce.Tick += async (_, _) =>
            {
                debounce.Stop();
                // Wait until the editor has closed its write handle.
                try
                {
                    using var probe = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                catch
                {
                    debounce.Start();
                    return;
                }

                try
                {
                    var client = _client;
                    if (client is null) return;
                    await client.UploadAsync(local, entry.FullPath, null, CancellationToken.None);
                    _status.Text = $"Uploaded {entry.Name} at {DateTime.Now:HH:mm:ss}";
                    Reload();
                }
                catch (Exception ex)
                {
                    _status.Text = "Upload failed: " + ex.Message;
                }
            };

            watcher.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                debounce.Stop();
                debounce.Start();
            }));

            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            EndTransfer("Edit failed: " + ex.Message);
        }
    }

    private string? AskSave(string name)
    {
        var dialog = new SaveFileDialog { FileName = name, Title = "Download file" };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private async void Upload()
    {
        if (_client is null) return;
        var dialog = new OpenFileDialog { Title = "Upload files", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await UploadFiles(dialog.FileNames);
    }

    private async Task UploadFiles(IEnumerable<string> files)
    {
        if (_client is null) return;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var remote = _cwd.TrimEnd('/') + "/" + name;
            try
            {
                BeginTransfer("Uploading " + name);
                await _client.UploadAsync(file, remote,
                    (done, total) => Dispatcher.BeginInvoke(new Action(() => SetProgress(done, total))), CancellationToken.None);
                EndTransfer("Uploaded " + name);
            }
            catch (Exception ex)
            {
                EndTransfer("Upload failed: " + ex.Message);
            }
        }
        Reload();
    }

    private void BeginTransfer(string message, bool indeterminate = false)
    {
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = indeterminate;
        _progress.Value = 0;
        _status.Text = message;
    }

    private void SetProgress(long done, long total)
    {
        _progress.Value = total <= 0 ? 0 : Math.Min(100, done * 100.0 / total);
        _status.Text = $"{SftpEntry.FormatSize(done)} of {SftpEntry.FormatSize(total)}";
    }

    private void EndTransfer(string message)
    {
        _progress.IsIndeterminate = false;
        _progress.Visibility = Visibility.Collapsed;
        _progress.Value = 0;
        _status.Text = message;
    }

    private async void NewFolder()
    {
        if (_client is null) return;
        var name = PromptDialog.Show(Window.GetWindow(this), "New folder", "Folder name:", "new-folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _client.MkdirAsync(_cwd.TrimEnd('/') + "/" + name.Trim());
            Reload();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async void RenameSelected()
    {
        if (_client is null || _list.SelectedItem is not SftpEntry entry) return;
        var name = PromptDialog.Show(Window.GetWindow(this), "Rename", "New name:", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        try
        {
            await _client.RenameAsync(entry.FullPath, _cwd.TrimEnd('/') + "/" + name.Trim());
            Reload();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async void DeleteSelected()
    {
        if (_client is null) return;
        var entries = _list.SelectedItems.OfType<SftpEntry>().ToList();
        if (entries.Count == 0) return;

        var result = MessageBox.Show(Window.GetWindow(this),
            entries.Count == 1 ? $"Delete \"{entries[0].Name}\"?" : $"Delete {entries.Count} items?",
            "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        foreach (var entry in entries)
        {
            try
            {
                if (entry.IsDirectory) await _client.RmdirAsync(entry.FullPath);
                else await _client.RemoveAsync(entry.FullPath);
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        }
        Reload();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is null) return;
        e.Handled = true;
        await UploadFiles(files.Where(File.Exists));
    }

    private static string Sanitise(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
