using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt.Views;

/// <summary>
/// Snippet library: folders, placeholders, run-on-one-pane, run-on-every-pane and insert without
/// pressing Enter. Everything is edited here — there is no second snippet editor in Options.
/// </summary>
public sealed class SnippetWindow : Window
{
    private readonly DataStore _store;
    private readonly IReadOnlyList<TerminalPane> _panes;
    private readonly TerminalPane? _active;

    private readonly ListBox _list = new() { Height = 190 };
    private readonly TextBox _filter = new() { Width = 200 };
    private readonly ComboBox _folderFilter = new() { Width = 170 };
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new();
    private readonly TextBox _command = new() { Height = 88, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _description = new();
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly List<Snippet> _visible = new();
    private bool _loading;
    private Snippet? _editing;

    public SnippetWindow(DataStore store, IReadOnlyList<TerminalPane>? panes, TerminalPane? active)
    {
        _store = store;
        _panes = panes ?? (active is null ? Array.Empty<TerminalPane>() : new[] { active });
        _active = active ?? _panes.FirstOrDefault();

        Title = "Snippets";
        Width = 720;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        _filter.TextChanged += (_, _) => Rebuild();
        _folderFilter.SelectionChanged += (_, _) => Rebuild();
        _list.SelectionChanged += (_, _) => LoadSelected();
        _list.MouseDoubleClick += (_, _) => RunHere();

        Content = BuildLayout();
        RefreshFolders();
        Rebuild();
        if (_visible.Count > 0) _list.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------- layout

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        for (var i = 0; i < 6; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(DialogChrome.Heading("Snippets"));
        heading.Children.Add(new TextBlock
        {
            Text = "Placeholders are filled from the session when you run: " + string.Join(" ", SnippetText.Tokens) +
                   ". A placeholder with no value is left visible rather than blanked.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(heading);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        filters.Children.Add(new TextBlock { Text = "Filter", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        filters.Children.Add(_filter);
        filters.Children.Add(new TextBlock { Text = "Folder", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        filters.Children.Add(_folderFilter);
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        Grid.SetRow(_list, 2);
        root.Children.Add(_list);

        var editor = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        editor.Children.Add(Label("Name", 0, 0));
        editor.Children.Add(_name);
        Grid.SetRow(_name, 1);
        Grid.SetColumn(_name, 0);
        editor.Children.Add(Label("Command", 0, 2));
        _command.Margin = new Thickness(0, 0, 0, 0);
        editor.Children.Add(_command);
        Grid.SetRow(_command, 3);
        Grid.SetColumn(_command, 0);

        editor.Children.Add(Label("Folder", 2, 0));
        editor.Children.Add(_folder);
        Grid.SetRow(_folder, 1);
        Grid.SetColumn(_folder, 2);
        editor.Children.Add(Label("Description", 2, 2));
        editor.Children.Add(_description);
        Grid.SetRow(_description, 3);
        Grid.SetColumn(_description, 2);

        var editButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        editButtons.Children.Add(Small("New", NewSnippet));
        editButtons.Children.Add(Small("Save", SaveSnippet));
        editButtons.Children.Add(Small("Delete", DeleteSnippet));
        Grid.SetRow(editButtons, 4);
        Grid.SetColumnSpan(editButtons, 3);
        Grid.SetColumn(editButtons, 0);
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.Children.Add(editButtons);

        Grid.SetRow(editor, 3);
        root.Children.Add(editor);

        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _status.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var here = new Button
        {
            Content = "Run in active pane",
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            Height = 32,
            MinWidth = 150
        };
        here.Click += (_, _) => RunHere();
        actions.Children.Add(here);

        var all = new Button { Content = "Run in all panes", Height = 32, MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
        all.Click += (_, _) => RunAll();
        actions.Children.Add(all);

        var insert = new Button { Content = "Insert (no Enter)", Height = 32, MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
        insert.Click += (_, _) => Insert();
        actions.Children.Add(insert);

        var close = new Button { Content = "Close", Height = 32, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);

        Grid.SetRow(actions, 5);
        root.Children.Add(actions);
        return root;
    }

    private static TextBlock Label(string text, int column, int row)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        Grid.SetColumn(label, column);
        Grid.SetRow(label, row);
        return label;
    }

    private static Button Small(string text, Action action)
    {
        var button = new Button { Content = text, Height = 30, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    // ---------------------------------------------------------------- data

    private void RefreshFolders()
    {
        var previous = _folderFilter.SelectedItem as string;
        _folderFilter.Items.Clear();
        _folderFilter.Items.Add("All folders");
        foreach (var folder in _store.Snippets
                     .Select(s => s.Folder)
                     .Where(f => !string.IsNullOrWhiteSpace(f))
                     .Select(f => f!.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            _folderFilter.Items.Add(folder);
        _folderFilter.Items.Add("(no folder)");
        _folderFilter.SelectedItem = previous is not null && _folderFilter.Items.Contains(previous) ? previous : "All folders";

    }

    private void Rebuild()
    {
        var filter = _filter.Text.Trim();
        var folderFilter = _folderFilter.SelectedItem as string ?? "All folders";

        _visible.Clear();
        foreach (var snippet in _store.Snippets
                     .OrderBy(s => string.IsNullOrWhiteSpace(s.Folder) ? 1 : 0)
                     .ThenBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (folderFilter == "(no folder)" && !string.IsNullOrWhiteSpace(snippet.Folder)) continue;
            if (folderFilter is not "All folders" and not "(no folder)" &&
                !string.Equals(snippet.Folder?.Trim(), folderFilter, StringComparison.OrdinalIgnoreCase)) continue;

            if (filter.Length > 0)
            {
                var haystack = snippet.Name + " " + snippet.Command + " " + snippet.Description;
                if (haystack.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }
            _visible.Add(snippet);
        }

        var keep = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var snippet in _visible)
        {
            var folder = string.IsNullOrWhiteSpace(snippet.Folder) ? "" : "[" + snippet.Folder!.Trim() + "] ";
            var command = snippet.Command.Replace("\r", " ").Replace("\n", " ");
            if (command.Length > 70) command = command[..70] + "…";
            _list.Items.Add(folder + snippet.Name + "  —  " + command);
        }
        if (_visible.Count > 0) _list.SelectedIndex = Math.Clamp(keep, 0, _visible.Count - 1);
        UpdateStatus();
    }

    private void LoadSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _visible.Count) return;
        _loading = true;
        _editing = _visible[_list.SelectedIndex];
        _name.Text = _editing.Name;
        _folder.Text = _editing.Folder ?? "";
        _command.Text = _editing.Command;
        _description.Text = _editing.Description ?? "";
        _loading = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var target = _active is null ? "no active pane" : _active.Session.Name;
        var count = _panes.Count;
        _status.Text = $"{_visible.Count} snippet(s) · runs in {target}" +
                       (count > 1 ? $" · {count} panes in this tab" : "");
    }

    // ---------------------------------------------------------------- editing

    private void NewSnippet()
    {
        var snippet = new Snippet
        {
            Name = "New snippet",
            Command = "",
            Folder = (_folderFilter.SelectedItem as string) is { } f && f is not "All folders" and not "(no folder)" ? f : null
        };
        _store.Snippets.Add(snippet);
        _store.SaveSnippets();
        RefreshFolders();
        Rebuild();
        _list.SelectedIndex = _visible.IndexOf(snippet);
        _name.Focus();
        _name.SelectAll();
    }

    private void SaveSnippet()
    {
        if (_editing is null) { NewSnippet(); return; }
        var name = _name.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(this, "A snippet needs a name.", "SSH-it"); return; }
        if (_command.Text.Trim().Length == 0) { MessageBox.Show(this, "A snippet needs a command.", "SSH-it"); return; }

        _editing.Name = name;
        _editing.Folder = string.IsNullOrWhiteSpace(_folder.Text) ? null : _folder.Text.Trim();
        _editing.Command = _command.Text;
        _editing.Description = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text;
        _store.SaveSnippets();
        RefreshFolders();
        Rebuild();
        _status.Text = "Saved.";
    }

    private void DeleteSnippet()
    {
        if (_editing is null) return;
        if (MessageBox.Show(this, $"Delete \"{_editing.Name}\"?", "SSH-it",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _store.Snippets.Remove(_editing);
        _store.SaveSnippets();
        _editing = null;
        RefreshFolders();
        Rebuild();
        if (_visible.Count > 0) _list.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------- running

    private string? Expanded()
    {
        if (_editing is null || string.IsNullOrWhiteSpace(_editing.Command)) return null;
        // Running reads the stored snippet; edits are only persisted by the Save button, so a run
        // can never quietly rewrite a folder or description from a half-loaded editor.
        return SnippetText.Expand(_editing.Command, _active?.Session, _active?.WorkingDirectory);
    }

    private void RunHere()
    {
        var text = Expanded();
        if (text is null || _active is null) return;
        _active.SendCommand(text);
        _status.Text = "Sent to " + _active.Session.Name + ".";
    }

    private void RunAll()
    {
        var text = Expanded();
        if (text is null) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\r") + "\r");
        var sent = 0;
        foreach (var pane in _panes)
        {
            // Raw writes: the broadcast path would re-send this to every pane again.
            if (pane.SendRaw(bytes)) sent++;
        }
        _status.Text = $"Sent to {sent} pane(s).";
    }

    private void Insert()
    {
        var text = Expanded();
        if (text is null || _active is null) return;
        _active.SendInput(text, submit: false);
        _status.Text = "Inserted without Enter — finish the line in the terminal.";
    }
}
