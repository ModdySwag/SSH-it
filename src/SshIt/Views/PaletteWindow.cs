using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// R3-23: the command palette window. It draws what CommandPalette builds and reports the chosen row;
/// all matching lives in the model so it can be tested without a window.
///
/// The query box keeps focus for the whole session: Up/Down move the list selection, Enter runs the
/// selected row, Esc closes. That is why the keys are handled on PreviewKeyDown here rather than on
/// the list — a TextBox otherwise eats the arrow keys for caret movement.
///
/// Enter never types into the terminal by itself: session and action rows do their own thing, while a
/// snippet or a history row is INSERTED at the prompt with no Enter, so a misfire is visible and can
/// be cancelled. A palette that presses Enter for you turns one stray keystroke into a command.
/// </summary>
public sealed class PaletteWindow : Window
{
    private readonly Func<IReadOnlyList<PaletteItem>> _source;
    private readonly Action<PaletteItem> _execute;
    private readonly TextBox _query = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _footer = new();
    private readonly TextBlock _empty = new();
    private List<PaletteItem> _all = new();
    private List<PaletteItem> _shown = new();

    public PaletteWindow(Func<IReadOnlyList<PaletteItem>> source, Action<PaletteItem> execute)
    {
        _source = source;
        _execute = execute;

        Title = "Command palette — SSH-it";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        Width = 720;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var windowBrush = (Brush)Application.Current.FindResource("Brush.Window");
        var borderBrush = (Brush)Application.Current.FindResource("Brush.BorderStrong");
        var textBrush = (Brush)Application.Current.FindResource("Brush.Text");
        var mutedBrush = (Brush)Application.Current.FindResource("Brush.TextMuted");

        _query.FontSize = 16;
        _query.FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        _query.Foreground = textBrush;
        _query.Background = Brushes.Transparent;
        _query.BorderThickness = new Thickness(0);
        _query.Height = 44;
        _query.Padding = new Thickness(16, 0, 16, 0);
        _query.VerticalContentAlignment = VerticalAlignment.Center;
        _query.CaretBrush = textBrush;
        _query.SelectionBrush = (Brush)Application.Current.FindResource("Brush.Accent");
        _query.SelectionOpacity = 0.35;
        // The theme's TextBox template draws a rounded frame and centres its content host through a
        // Padding margin; inside the palette's own frame that combination clips the text away (the
        // box was 48 px tall with an empty-looking 24 px viewport). A plain TextBox with no chrome
        // renders the query reliably at any size.
        _query.Style = null;
        AutomationProperties.SetAutomationId(_query, "PaletteQuery");
        AutomationProperties.SetName(_query, "Command palette query");
        _query.TextChanged += (_, _) => Refresh();

        var prompt = new TextBlock
        {
            Text = ">",
            FontSize = 16,
            Foreground = (Brush)Application.Current.FindResource("Brush.Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };

        var queryRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(prompt, Dock.Left);
        queryRow.Children.Add(prompt);
        queryRow.Children.Add(_query);

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Padding = new Thickness(6, 4, 6, 4);
        _list.SelectionMode = SelectionMode.Single;
        AutomationProperties.SetAutomationId(_list, "PaletteList");
        AutomationProperties.SetName(_list, "Palette results");
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem item) item.BringIntoView();
        };
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindRow(source)?.Tag is PaletteItem row)
            {
                e.Handled = true;
                Choose(row);
            }
        };

        _empty.Text = "Nothing matches. Fewer words usually help; '>' searches commands and settings only.";
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Foreground = mutedBrush;
        _empty.Margin = new Thickness(20, 18, 20, 18);
        _empty.Visibility = Visibility.Collapsed;

        _footer.FontSize = 11;
        _footer.Foreground = mutedBrush;
        _footer.Padding = new Thickness(16, 7, 16, 7);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = queryRow
        };
        Grid.SetRow(head, 0);
        body.Children.Add(head);

        var stack = new Grid();
        stack.Children.Add(_list);
        stack.Children.Add(_empty);
        Grid.SetRow(stack, 1);
        body.Children.Add(stack);

        var foot = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _footer
        };
        Grid.SetRow(foot, 2);
        body.Children.Add(foot);

        var frame = new Border
        {
            Background = windowBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black
            },
            Child = body
        };

        Content = frame;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { Place(); _all = (_source() ?? Array.Empty<PaletteItem>()).ToList(); Refresh(); };
        Activated += (_, _) => _query.Focus();
    }

    /// <summary>Fills the query before the window is shown; the smoke hook uses it.</summary>
    public void Preload(string text)
    {
        _query.Text = text;
        _query.CaretIndex = text.Length;
    }

    /// <summary>Over the owner, a little below its title bar, inside the work area.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        var owner = Owner;
        var width = Math.Min(Width, Math.Max(420, area.Width - 80));
        Width = width;

        var left = owner is null ? area.Left + (area.Width - width) / 2 : owner.Left + (owner.Width - width) / 2;
        var top = owner is null ? area.Top + 120 : owner.Top + Math.Min(120, Math.Max(60, owner.Height * 0.12));

        Left = Math.Clamp(left, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8));
        Top = Math.Clamp(top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - 120));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;

            case Key.Down:
                Move(1);
                e.Handled = true;
                break;

            case Key.PageUp:
                Move(-8);
                e.Handled = true;
                break;

            case Key.PageDown:
                Move(8);
                e.Handled = true;
                break;

            case Key.Enter:
                if (_list.SelectedItem is ListBoxItem item && item.Tag is PaletteItem chosen) Choose(chosen);
                e.Handled = true;
                break;

            case Key.Tab:
                Move(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_shown.Count == 0) return;
        var next = Math.Clamp(_list.SelectedIndex + delta, 0, _shown.Count - 1);
        _list.SelectedIndex = next;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Choose(PaletteItem item)
    {
        // Close first: the action may open a modal dialog of its own, and a palette that is still
        // alive underneath would take the focus back when that dialog closes.
        Close();
        _execute(item);
    }

    private void Refresh()
    {
        _shown = CommandPalette.Rank(_all, _query.Text);
        _list.Items.Clear();

        foreach (var item in _shown)
        {
            var header = new TextBlock
            {
                Text = item.Title,
                FontSize = 13.5,
                Foreground = (Brush)Application.Current.FindResource("Brush.Text"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var subtitle = new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var kind = new TextBlock
            {
                Text = CommandPalette.KindLabel(item.Kind),
                FontSize = 10.5,
                Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Width = 60,
                TextAlignment = TextAlignment.Right
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetColumn(kind, 0);
            Grid.SetRowSpan(kind, 2);
            Grid.SetColumn(header, 1);
            Grid.SetColumn(subtitle, 1);
            Grid.SetRow(subtitle, 1);
            grid.Children.Add(kind);
            grid.Children.Add(header);
            grid.Children.Add(subtitle);

            var row = new ListBoxItem
            {
                Content = grid,
                Tag = item,
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(row, CommandPalette.KindLabel(item.Kind) + ": " + item.Title);

            _list.Items.Add(row);
        }

        _empty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shown.Count > 0) _list.SelectedIndex = 0;

        _footer.Text = _all.Count == 0
            ? "Loading…"
            : _shown.Count + " of " + _all.Count + " items   ·   ↑↓ select   ·   Enter run   ·   Esc close"
              + "   ·   '>' commands only";
    }

    private static ListBoxItem? FindRow(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is ListBoxItem item) return item;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
