using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;
using Microsoft.Win32;

namespace SshIt.Views;

public partial class SessionEditorWindow : Window
{
    private readonly DataStore _store;
    private readonly bool _isNew;
    private string? _colorTag;

    public Session Session { get; }

    public SessionEditorWindow(Session session, DataStore store, bool isNew)
    {
        InitializeComponent();
        Session = session;
        _store = store;
        _isNew = isNew;

        HeadingText.Text = isNew ? "New session" : "Edit session";
        SaveButton.Content = isNew ? "Add session" : "Save";

        NameBox.Text = session.Name;
        HostBox.Text = session.Host;
        PortBox.Text = session.Port.ToString();
        UserBox.Text = session.User;
        KeyBox.Text = session.KeyPath ?? "";
        JumpBox.Text = JumpChain.Format(session.JumpHost, session.JumpKeys);
        StartupBox.Text = session.StartupCommand ?? "";
        NotesBox.Text = session.Notes ?? "";
        AliasBox.Text = session.ConfigAlias ?? "";
        AliasCheck.IsChecked = session.UseConfigAlias;

        AgentForwardCheck.IsChecked = session.ForwardAgent;
        CompressionCheck.IsChecked = session.Compression;
        StrictCheck.IsChecked = session.StrictHostKeyChecking;
        AllowAgentCheck.IsChecked = session.AllowAgent;
        X11Check.IsChecked = session.X11Forwarding;
        UpdateX11Hint();

        // IsLocal wins over Kind: older session files carry Kind "Ssh" alongside IsLocal true, and
        // trusting Kind turned a local PowerShell tab into an SSH-to-localhost tab on save.
        SelectTag(KindBox, session.IsLocal ? "Local"
            : string.IsNullOrWhiteSpace(session.Kind) ? "Ssh" : session.Kind);
        SerialPortBox.Text = session.SerialPortName ?? "";
        SerialBaudBox.Text = session.SerialBaud.ToString();
        TmuxSessionBox.Text = session.TmuxSession ?? "";

        FolderBox.Items.Add("(no folder)");
        foreach (var folder in _store.Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            FolderBox.Items.Add(folder.Name);
        var index = session.FolderId is null ? 0 : _store.Folders.FindIndex(f => f.Id == session.FolderId) + 1;
        FolderBox.SelectedIndex = Math.Clamp(index, 0, FolderBox.Items.Count - 1);

        _colorTag = SessionTag.Normalize(session.ColorTag);
        BuildTagPicker();

        SelectAuth(session.AuthMode);

        if (session.AuthMode == "Password")
        {
            var saved = CredentialVault.GetSessionSecret(session.Id);
            if (!string.IsNullOrEmpty(saved))
            {
                PasswordBox.Password = saved;
                SaveSecretCheck.IsChecked = true;
            }
        }

        UpdateRowVisibility();
    }

    private void SelectAuth(string mode)
    {
        for (var i = 0; i < AuthBox.Items.Count; i++)
        {
            if (AuthBox.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                AuthBox.SelectedIndex = i;
                return;
            }
        }
        AuthBox.SelectedIndex = 0;
    }

    private string AuthMode =>
        (AuthBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Agent";

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRowVisibility();

    private void AliasCheck_Changed(object sender, RoutedEventArgs e) => UpdateRowVisibility();

    private void UpdateRowVisibility()
    {
        if (KeyRow is null) return; // during InitializeComponent

        var auth = AuthMode;
        KeyRow.Visibility = auth == "Key" ? Visibility.Visible : Visibility.Collapsed;
        PasswordRow.Visibility = auth == "Password" ? Visibility.Visible : Visibility.Collapsed;
        SaveSecretCheck.Visibility = auth == "Password" ? Visibility.Visible : Visibility.Collapsed;

        var alias = AliasCheck.IsChecked == true;
        AliasBox.IsEnabled = alias;
        HostBox.IsEnabled = !alias;
        PortBox.IsEnabled = !alias;
        UserBox.IsEnabled = !alias;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a private key",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) KeyBox.Text = dialog.FileName;
    }

    private void X11Check_Changed(object sender, RoutedEventArgs e) => UpdateX11Hint();

    private static void SelectTag(ComboBox box, string tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    /// <summary>
    /// The probe runs off the UI thread: with no X server it can take half a second, and this
    /// is only a hint under a checkbox.
    /// </summary>
    private async void UpdateX11Hint()
    {
        if (X11Hint is null) return;

        if (X11Check.IsChecked != true)
        {
            X11Hint.Text = "X11 passes the remote program's window to a local X server.";
            return;
        }

        X11Hint.Text = "Looking for a local X server…";
        var text = await Task.Run(() =>
        {
            X11Support.ListeningPort(refresh: true);
            return X11Support.Describe();
        });
        X11Hint.Text = text;
    }

    /// <summary>
    /// The picker: one option per preset plus "no colour". A RadioButton (not a Border) so a screen
    /// reader can reach it, name it, and say which one is selected; its template is a bare
    /// ContentPresenter because the theme's own RadioButton chrome would draw its circle over the
    /// colour it is selling. The ring stays on the swatch Border for the same reason.
    /// </summary>
    private void BuildTagPicker()
    {
        TagPanel.Children.Clear();
        AddSwatch(null);
        foreach (var hex in SessionTag.Presets) AddSwatch(hex);
        PaintTagSelection();
    }

    private void AddSwatch(string? hex)
    {
        var swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = (Brush?)SessionTag.Brush(hex) ?? Brushes.Transparent,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent
        };
        if (hex is null)
        {
            swatch.Child = new TextBlock
            {
                Text = "\uE711",
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
            };
        }

        var template = new ControlTemplate(typeof(RadioButton));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));

        var option = new RadioButton
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 7, 0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = hex,
            Content = swatch,
            GroupName = "SessionColorTag",
            Template = template,
            ToolTip = hex is null ? "No colour tag" : SessionTag.Name(hex) + " tag",
            IsTabStop = true
        };
        System.Windows.Automation.AutomationProperties.SetName(option,
            hex is null ? "No colour tag" : SessionTag.Name(hex) + " tag");
        System.Windows.Automation.AutomationProperties.SetHelpText(option,
            hex is null ? "Clears the colour tag" : hex);
        System.Windows.Automation.AutomationProperties.SetAutomationId(option, "Tag" + (hex ?? "None"));

        option.Checked += (_, _) =>
        {
            if (string.Equals(_colorTag, hex, StringComparison.OrdinalIgnoreCase)) return;
            _colorTag = hex;
            PaintTagSelection();
        };
        option.GotKeyboardFocus += (_, _) => PaintTagSelection();
        option.LostKeyboardFocus += (_, _) => PaintTagSelection();

        TagPanel.Children.Add(option);
    }

    /// <summary>The ring is the only selection cue: a tick on a coloured chip would hide the colour.</summary>
    private void PaintTagSelection()
    {
        var ring = (Brush)Application.Current.FindResource("Brush.Text");
        var focusRing = (Brush)Application.Current.FindResource("Brush.Accent");
        foreach (var child in TagPanel.Children)
        {
            if (child is not RadioButton option) continue;
            var hex = (string?)option.Tag;
            var selected = string.Equals(hex, _colorTag, StringComparison.OrdinalIgnoreCase);
            if (option.IsChecked != selected) option.IsChecked = selected;
            if (option.Content is not Border swatch) continue;
            swatch.BorderBrush = selected ? ring
                : option.IsKeyboardFocused ? focusRing
                : Brushes.Transparent;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var alias = AliasCheck.IsChecked == true;
        if (alias)
        {
            if (string.IsNullOrWhiteSpace(AliasBox.Text))
            {
                ValidationText.Text = "Enter a config Host alias.";
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(HostBox.Text))
            {
                ValidationText.Text = "A host name is required.";
                return;
            }
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            ValidationText.Text = "Port must be between 1 and 65535.";
            return;
        }

        if (AuthMode == "Key" && string.IsNullOrWhiteSpace(KeyBox.Text))
        {
            ValidationText.Text = "Choose a private key file, or switch method to the SSH agent.";
            return;
        }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = alias ? AliasBox.Text.Trim() : $"{UserBox.Text.Trim()}@{HostBox.Text.Trim()}".TrimStart('@');

        Session.Name = name;
        Session.Host = HostBox.Text.Trim();
        Session.Port = port;
        Session.User = UserBox.Text.Trim();
        Session.UseConfigAlias = alias;
        Session.ConfigAlias = alias ? AliasBox.Text.Trim() : null;
        Session.KeyPath = AuthMode == "Key" ? KeyBox.Text.Trim() : null;
        Session.AuthMode = AuthMode;
        var (jumpHops, jumpKeys) = JumpChain.ParseSpec(JumpBox.Text);
        Session.JumpHost = jumpHops.Count == 0 ? null : string.Join(",", jumpHops);
        Session.JumpKeys = jumpKeys.Count == 0 ? null : jumpKeys;
        Session.StartupCommand = string.IsNullOrWhiteSpace(StartupBox.Text) ? null : StartupBox.Text.Trim();
        Session.Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();
        Session.ColorTag = _colorTag;
        Session.ForwardAgent = AgentForwardCheck.IsChecked == true;
        Session.Compression = CompressionCheck.IsChecked == true;
        Session.StrictHostKeyChecking = StrictCheck.IsChecked == true;
        Session.AllowAgent = AllowAgentCheck.IsChecked == true;
        Session.X11Forwarding = X11Check.IsChecked == true;

        Session.Kind = TagOf(KindBox) ?? "Ssh";
        Session.IsLocal = Session.Kind == "Local";
        Session.SerialPortName = string.IsNullOrWhiteSpace(SerialPortBox.Text) ? null : SerialPortBox.Text.Trim();
        Session.SerialBaud = int.TryParse(SerialBaudBox.Text.Trim(), out var baud) && baud > 0 ? baud : 115200;
        Session.TmuxSession = string.IsNullOrWhiteSpace(TmuxSessionBox.Text) ? null : TmuxSessionBox.Text.Trim();

        Session.FolderId = FolderBox.SelectedIndex <= 0
            ? null
            : _store.Folders[FolderBox.SelectedIndex - 1].Id;

        if (AuthMode == "Password")
        {
            var password = PasswordBox.Password;
            if (SaveSecretCheck.IsChecked == true && password.Length > 0)
            {
                CredentialVault.SaveSessionSecret(Session.Id, password);
                Session.AuthMode = "Password";
            }
            else if (SaveSecretCheck.IsChecked == false)
            {
                CredentialVault.DeleteSessionSecret(Session.Id);
            }
        }

        DialogResult = true;
    }
}
