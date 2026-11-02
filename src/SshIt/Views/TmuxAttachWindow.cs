using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// The small chooser behind Tools → tmux control mode…: a local WSL target (the one this machine
/// can actually prove against) or any saved SSH session, plus an optional tmux session name.
/// A saved session with Kind "Tmux" opens its own window directly and never sees this dialog.
/// </summary>
public sealed class TmuxAttachWindow : Window
{
    private readonly DataStore _store;
    private readonly ComboBox _target;
    private readonly TextBox _distro;
    private readonly TextBox _session;
    private readonly TextBlock _hint;

    public TmuxSessionPick? Pick { get; private set; }

    public TmuxAttachWindow(DataStore store)
    {
        _store = store;

        Title = "tmux control mode";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = "Attach to a tmux server in control mode. Every tmux pane becomes a real pane here; "
                   + "typing, resizing and the layout come from tmux itself.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        Grid.SetRow(intro, 0);
        root.Children.Add(intro);

        var targetRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.Children.Add(new TextBlock { Text = "Connect to", VerticalAlignment = VerticalAlignment.Center });
        _target = new ComboBox { Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(_target, "tmux target");
        _target.Items.Add(new ComboBoxItem { Content = "Local (WSL)", Tag = "" });
        foreach (var session in _store.Sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (session.Kind is "Telnet" or "Serial") continue;
            _target.Items.Add(new ComboBoxItem { Content = $"{session.Name} — {session.DisplayTarget}", Tag = session.Id });
        }
        _target.SelectedIndex = 0;
        _target.SelectionChanged += (_, _) => UpdateHint();
        Grid.SetColumn(_target, 1);
        targetRow.Children.Add(_target);
        Grid.SetRow(targetRow, 1);
        root.Children.Add(targetRow);

        var distroRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        distroRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        distroRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        distroRow.Children.Add(new TextBlock { Text = "WSL distribution", VerticalAlignment = VerticalAlignment.Center });
        _distro = new TextBox { Text = "Ubuntu", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(_distro, "WSL distribution");
        Grid.SetColumn(_distro, 1);
        distroRow.Children.Add(_distro);
        Grid.SetRow(distroRow, 2);
        root.Children.Add(distroRow);

        var sessionRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        sessionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        sessionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sessionRow.Children.Add(new TextBlock { Text = "tmux session", VerticalAlignment = VerticalAlignment.Center });
        _session = new TextBox { Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(_session, "tmux session name");
        Grid.SetColumn(_session, 1);
        sessionRow.Children.Add(_session);
        Grid.SetRow(sessionRow, 3);
        root.Children.Add(sessionRow);

        _hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 12),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        Grid.SetRow(_hint, 4);
        root.Children.Add(_hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var attach = new Button
        {
            Content = "Attach", Width = 104, IsDefault = true,
            Style = (Style)Application.Current.FindResource("Btn.Accent")
        };
        AutomationProperties.SetName(attach, "Attach tmux");
        attach.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(attach);

        var buttonRow = new Grid();
        Grid.SetRow(buttonRow, 5);
        buttonRow.Margin = new Thickness(0, 6, 0, 0);
        buttonRow.Children.Add(buttons);
        root.Children.Add(buttonRow);

        Content = root;
        UpdateHint();
        Loaded += (_, _) => _target.Focus();
    }

    private Session? SelectedSession
    {
        get
        {
            var id = (_target.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return string.IsNullOrEmpty(id) ? null : _store.Sessions.FirstOrDefault(s => s.Id == id);
        }
    }

    private void UpdateHint()
    {
        var local = SelectedSession is null;
        _distro.IsEnabled = local;
        _hint.Text = local
            ? "WSL must have tmux 3.6 or newer. `tmux new-session -d -s name` first if the session does not exist."
            : "Uses this session's SSH settings (key, agent, jump host) and runs `tmux -CC attach` on the remote host. "
              + "A blank session name attaches to the most recent session.";
    }

    private void Accept()
    {
        Pick = new TmuxSessionPick(SelectedSession,
            _session.Text.Trim(),
            SelectedSession is null ? _distro.Text.Trim() : null);
        DialogResult = true;
    }
}

/// <summary>What the chooser returns; the SSH half is null for the local WSL target.</summary>
public sealed record TmuxSessionPick(Session? SshSession, string TmuxSession, string? LocalDistro);
