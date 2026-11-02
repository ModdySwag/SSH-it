using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Terminal;

namespace SshIt.Views;

/// <summary>
/// A tab that has been pulled out of the main window. It owns its panes outright:
/// the TerminalPane objects (and therefore their ConPTY processes and buffers) are
/// moved here rather than recreated, so a live session keeps running across the move.
/// Closing this window ends those sessions.
/// </summary>
public sealed class DetachedPaneWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>The panes this window owns, for the agent bridge.</summary>
    internal IEnumerable<TerminalPane> Panes() => _panes;
    private readonly List<TerminalPane> _panes = new();
    private readonly Grid _host = new();
    private readonly TextBlock _status;

    public DetachedPaneWindow(IEnumerable<TerminalPane> panes, AppSettings settings, string title)
    {
        _settings = settings;
        _panes.AddRange(panes);

        Title = title + " — SSH-it";
        Width = 960;
        Height = 640;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_host, 0);
        root.Children.Add(_host);

        var bar = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.MenuBar"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        _status = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        bar.Child = _status;
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        Content = root;

        foreach (var pane in _panes)
        {
            pane.Notice += OnPaneNotice;
            pane.StateChanged += _ => Dispatcher.BeginInvoke(UpdateStatusText);
        }

        Rebuild();
        UpdateStatusText();
        Loaded += (_, _) => { if (_panes.Count > 0) _panes[0].FocusTerminal(); };
    }

    private void Rebuild()
    {
        _host.Children.Clear();

        if (_panes.Count == 0) return;

        if (_panes.Count == 1)
        {
            _host.RowDefinitions.Clear();
            _host.ColumnDefinitions.Clear();
            _host.Children.Add(_panes[0]);
            return;
        }

        // Two panes: stacked, with a splitter. Detached tabs are for parking a
        // session beside something else, so a rows split is the useful default.
        _host.RowDefinitions.Clear();
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var splitter = new GridSplitter
        {
            Height = 5,
            Background = (Brush)Application.Current.FindResource("Brush.Border"),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Rows
        };

        Grid.SetRow(_panes[0], 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(_panes[1], 2);
        _host.Children.Add(_panes[0]);
        _host.Children.Add(splitter);
        _host.Children.Add(_panes[1]);
    }

    private void OnPaneNotice(TerminalPane pane, string what)
    {
        if (what != "close") return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_panes.Count <= 1)
            {
                Close();
                return;
            }

            _panes.Remove(pane);
            pane.Dispose();
            Rebuild();
            UpdateStatusText();
        });
    }

    private void UpdateStatusText()
    {
        if (_panes.Count == 0)
        {
            _status.Text = "Detached tab";
            return;
        }

        var pane = _panes[0];
        _status.Text = _panes.Count > 1
            ? $"{pane.Session.Name} · {_panes.Count} panes · detached window"
            : $"{pane.Session.Name} · {pane.State} · detached window";
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var pane in _panes.ToList())
        {
            pane.Notice -= OnPaneNotice;
            try { pane.Dispose(); } catch { }
        }
        _panes.Clear();
        base.OnClosed(e);
    }
}
