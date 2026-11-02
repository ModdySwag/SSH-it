using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>Local / remote / dynamic port forwards, driven by `ssh -N`.</summary>
public sealed class TunnelPanel : UserControl
{
    private readonly DataStore _store;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _title = new();
    private Session? _session;

    public TunnelPanel(MainWindow owner, DataStore store, Session? session)
    {
        _store = store;
        SetResourceReference(BackgroundProperty, "Brush.Panel");

        _title.Text = "Port forwarding";
        _title.FontSize = 13;
        _title.FontWeight = FontWeights.SemiBold;

        _status.FontSize = 11;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 3, 0, 10);

        var add = new Button
        {
            Content = "New tunnel",
            Height = 30,
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        add.Click += (_, _) => EditTunnel(null);

        var header = new StackPanel { Margin = new Thickness(14, 12, 14, 10) };
        header.Children.Add(_title);
        header.Children.Add(_status);
        header.Children.Add(add);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(10, 4, 8, 10)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroll);
        Content = grid;

        SetSession(session);
    }

    public void SetSession(Session? session)
    {
        _session = session;
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();

        if (_session is null)
        {
            _status.Text = "Select a session to manage its tunnels.";
            return;
        }

        var tunnels = _store.Tunnels.Where(t => t.SessionId == _session.Id).ToList();
        _status.Text = tunnels.Count == 0
            ? $"No tunnels for {_session.DisplayTarget}."
            : $"{_session.DisplayTarget} · {tunnels.Count} tunnel(s)";

        foreach (var tunnel in tunnels)
            _rows.Children.Add(BuildRow(tunnel));
    }

    private Border BuildRow(TunnelDef tunnel)
    {
        var running = TunnelService.IsRunning(tunnel.Id);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Fill = (Brush)Application.Current.FindResource(running ? "Brush.Success" : "Brush.TextDisabled")
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tunnel.Name) ? tunnel.Summary : tunnel.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = tunnel.Summary,
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        var toggle = new Button
        {
            Content = running ? "Stop" : "Start",
            Height = 28,
            Width = 64,
            Margin = new Thickness(8, 0, 0, 0)
        };
        toggle.Click += (_, _) =>
        {
            if (TunnelService.IsRunning(tunnel.Id)) TunnelService.Stop(tunnel.Id);
            else
            {
                var error = TunnelService.Start(tunnel, _session!);
                if (error is not null) _status.Text = error;
            }
            Rebuild();
        };

        var more = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE712",
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
                FontSize = 11
            },
            Width = 28,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Btn.Icon")
        };
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit…" };
        edit.Click += (_, _) => EditTunnel(tunnel);
        var remove = new MenuItem { Header = "Delete" };
        remove.Click += (_, _) =>
        {
            TunnelService.Stop(tunnel.Id);
            _store.Tunnels.Remove(tunnel);
            _store.SaveTunnels();
            Rebuild();
        };
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(toggle);
        buttons.Children.Add(more);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.PanelAlt"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 9, 8, 9),
            Margin = new Thickness(2, 0, 2, 6),
            Child = grid
        };
    }

    private void EditTunnel(TunnelDef? existing)
    {
        if (_session is null) return;
        var draft = existing ?? new TunnelDef { SessionId = _session.Id, Name = "New tunnel" };
        var window = new TunnelEditorWindow(draft) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;

        if (existing is null)
        {
            _store.Tunnels.Add(window.Tunnel);
        }
        _store.SaveTunnels();
        Rebuild();
    }
}

public static class TunnelService
{
    private static readonly Dictionary<string, Process> Running = new();

    public static bool IsRunning(string tunnelId)
        => Running.TryGetValue(tunnelId, out var process) && process is { HasExited: false };

    public static string? Start(TunnelDef tunnel, Session session)
    {
        if (IsRunning(tunnel.Id)) return null;

        var forward = tunnel.Kind switch
        {
            TunnelKind.Local => $"-L {tunnel.BindAddress}:{tunnel.BindPort}:{tunnel.DestHost}:{tunnel.DestPort}",
            TunnelKind.Remote => $"-R {tunnel.BindAddress}:{tunnel.BindPort}:{tunnel.DestHost}:{tunnel.DestPort}",
            _ => $"-D {tunnel.BindAddress}:{tunnel.BindPort}"
        };

        var args = $"-N -o ExitOnForwardFailure=yes {forward} {SshLauncher.BuildBatchArguments(session)}";

        try
        {
            var info = new ProcessStartInfo(SshLauncher.SshExe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var secret = CredentialVault.GetSessionSecret(session.Id);
            if (!string.IsNullOrEmpty(secret))
            {
                info.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? "";
                info.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                info.Environment["CONDUIT_ASKPASS"] = CredentialVault.StoreAskPass(session.Id, secret);
            }

            var process = Process.Start(info);
            if (process is null) return "ssh.exe could not be started.";
            Running[tunnel.Id] = process;
            tunnel.IsActive = true;

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                tunnel.IsActive = false;
                Running.Remove(tunnel.Id);
            };
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("tunnel: " + ex.Message);
            return ex.Message;
        }
    }

    public static void Stop(string tunnelId)
    {
        if (!Running.TryGetValue(tunnelId, out var process)) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        Running.Remove(tunnelId);
    }

    public static void StopAll()
    {
        foreach (var id in Running.Keys.ToList()) Stop(id);
    }
}

/// <summary>Small form for a forwarding rule.</summary>
public sealed class TunnelEditorWindow : Window
{
    public TunnelDef Tunnel { get; }
    private readonly ComboBox _kind = new();
    private readonly TextBox _name = new(), _bindAddress = new(), _bindPort = new(), _destHost = new(), _destPort = new();
    private readonly CheckBox _autoStart = new() { Content = "Start when the session connects" };

    public TunnelEditorWindow(TunnelDef tunnel)
    {
        Tunnel = tunnel;
        Title = "Port forwarding rule";
        Width = 460;
        Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        foreach (var item in new[] { ("Local (-L)", TunnelKind.Local), ("Remote (-R)", TunnelKind.Remote), ("Dynamic SOCKS (-D)", TunnelKind.Dynamic) })
            _kind.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 });
        _kind.SelectedIndex = (int)tunnel.Kind;

        _name.Text = tunnel.Name;
        _bindAddress.Text = tunnel.BindAddress;
        _bindPort.Text = tunnel.BindPort.ToString();
        _destHost.Text = tunnel.DestHost;
        _destPort.Text = tunnel.DestPort.ToString();
        _autoStart.IsChecked = tunnel.AutoStart;

        var stack = new StackPanel { Margin = new Thickness(20) };
        Add(stack, "Name", _name);
        Add(stack, "Type", _kind);
        Add(stack, "Listen address", _bindAddress);
        Add(stack, "Listen port", _bindPort);
        Add(stack, "Destination host", _destHost);
        Add(stack, "Destination port", _destPort);
        stack.Children.Add(_autoStart);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Width = 88, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save", Width = 96, IsDefault = true, Style = (Style)Application.Current.FindResource("Btn.Accent") };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        Content = stack;
    }

    private static void Add(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 8, 0, 4)
        });
        control.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(control);
    }

    private void Save()
    {
        var kindTag = (_kind.SelectedItem as ComboBoxItem)?.Tag;
        Tunnel.Kind = kindTag is TunnelKind kind ? kind : TunnelKind.Local;
        Tunnel.Name = _name.Text.Trim();
        Tunnel.BindAddress = string.IsNullOrWhiteSpace(_bindAddress.Text) ? "127.0.0.1" : _bindAddress.Text.Trim();
        Tunnel.BindPort = int.TryParse(_bindPort.Text, out var bp) ? bp : 8080;
        Tunnel.DestHost = string.IsNullOrWhiteSpace(_destHost.Text) ? "localhost" : _destHost.Text.Trim();
        Tunnel.DestPort = int.TryParse(_destPort.Text, out var dp) ? dp : 80;
        Tunnel.AutoStart = _autoStart.IsChecked == true;
        if (string.IsNullOrWhiteSpace(Tunnel.Name)) Tunnel.Name = Tunnel.Summary;
        DialogResult = true;
    }
}
