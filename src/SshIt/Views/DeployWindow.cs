using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// B7: safe deploy on the surface people actually use. The CLI has had it since the sweep; the Files
/// panel only had Upload, which is the one operation that will happily replace sshd_config with a
/// typo. This dialog shows the exact target, the validator that will run (and what it will run on),
/// then reports either a validated swap or a refusal in the validator's own words.
/// </summary>
public sealed class DeployWindow : Window
{
    private readonly Session _session;
    private readonly SftpClient _client;
    private readonly string _localPath;
    private readonly string _remotePath;

    private readonly TextBox _validator = new();
    private readonly TextBlock _preview = new();
    private readonly CheckBox _dryRun = new();
    private readonly CheckBox _force = new();
    private readonly TextBox _result = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _deploy = new();
    private readonly Button _close = new();

    /// <summary>What the last run produced; null until Deploy is pressed.</summary>
    public DeployResult? Result { get; private set; }

    public DeployWindow(Session session, SftpClient client, string localPath, string remotePath)
    {
        _session = session;
        _client = client;
        _localPath = localPath;
        _remotePath = remotePath;

        Title = "Deploy " + Path.GetFileName(localPath);
        Width = 660;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var grid = new Grid { Margin = new Thickness(18) };
        for (var i = 0; i < 9; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var source = Caption("Local file");
        var sourceValue = Value(localPath);
        var target = Caption("Remote target");
        var targetValue = Value(session.DisplayTarget + ":" + remotePath);
        var validatorCaption = Caption("Validator (runs on the staged file, before anything is replaced)");

        _validator.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _validator.FontSize = 12;
        _validator.Padding = new Thickness(9, 0, 9, 0);
        _validator.Height = 32;
        _validator.VerticalContentAlignment = VerticalAlignment.Center;
        _validator.Margin = new Thickness(0, 4, 0, 2);
        _validator.Text = SafeDeploy.ValidatorFor(remotePath) ?? "";
        _validator.TextChanged += (_, _) => RefreshPreview();
        AutomationProperties.SetAutomationId(_validator, "DeployValidator");
        AutomationProperties.SetName(_validator, "Validator");

        _preview.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _preview.FontSize = 11;
        _preview.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _preview.TextWrapping = TextWrapping.Wrap;
        _preview.Margin = new Thickness(0, 0, 0, 10);

        _dryRun.Content = "Dry run";
        _dryRun.ToolTip = "Show what would be replaced and which backup name would be used. Nothing is uploaded.";
        _dryRun.FontSize = 12;
        _dryRun.Margin = new Thickness(0, 0, 18, 0);
        _dryRun.Checked += (_, _) => RefreshPreview();
        _dryRun.Unchecked += (_, _) => RefreshPreview();
        AutomationProperties.SetAutomationId(_dryRun, "DeployDryRun");

        _force.Content = "Force (no validation)";
        _force.ToolTip = "Write the file without a validator. The result says so, and never reads as validated.";
        _force.FontSize = 12;
        AutomationProperties.SetAutomationId(_force, "DeployForce");

        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        options.Children.Add(_dryRun);
        options.Children.Add(_force);

        _progress.Height = 4;
        _progress.Visibility = Visibility.Collapsed;
        _progress.Margin = new Thickness(0, 0, 0, 10);

        _result.IsReadOnly = true;
        _result.AcceptsReturn = true;
        _result.TextWrapping = TextWrapping.Wrap;
        _result.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _result.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _result.FontSize = 12;
        _result.Padding = new Thickness(10);
        _result.Height = 132;
        _result.Background = (Brush)Application.Current.FindResource("Brush.TerminalBackground");
        _result.Foreground = (Brush)Application.Current.FindResource("Brush.TerminalForeground");
        _result.BorderBrush = (Brush)Application.Current.FindResource("Brush.Border");
        _result.BorderThickness = new Thickness(1);
        AutomationProperties.SetAutomationId(_result, "DeployResult");
        AutomationProperties.SetName(_result, "Deploy result");

        _deploy.Content = "Deploy";
        _deploy.MinWidth = 96;
        _deploy.Height = 28;
        _deploy.Padding = new Thickness(14, 0, 14, 0);
        _deploy.Margin = new Thickness(0, 0, 8, 0);
        _deploy.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _deploy.IsDefault = true;
        _deploy.Click += (_, _) => RunDeploy();
        AutomationProperties.SetAutomationId(_deploy, "DeployGo");

        _close.Content = "Close";
        _close.MinWidth = 84;
        _close.Height = 28;
        _close.Padding = new Thickness(14, 0, 14, 0);
        _close.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _close.IsCancel = true;
        _close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_deploy);
        buttons.Children.Add(_close);

        var stack = new StackPanel();
        stack.Children.Add(source);
        stack.Children.Add(sourceValue);
        stack.Children.Add(target);
        stack.Children.Add(targetValue);
        stack.Children.Add(validatorCaption);
        stack.Children.Add(_validator);
        stack.Children.Add(_preview);
        stack.Children.Add(options);
        stack.Children.Add(_progress);
        stack.Children.Add(_result);

        grid.Children.Add(stack);
        var buttonRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        buttonRow.Children.Add(buttons);
        Grid.SetRow(buttonRow, 1);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(buttonRow);

        Content = grid;
        RefreshPreview();
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
        Margin = new Thickness(0, 8, 0, 2)
    };

    private static TextBlock Value(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Shows the command that would run, with the staged path already substituted, so a
    /// validator that ignores {file} is visible before the button is pressed rather than after.</summary>
    private void RefreshPreview()
    {
        var template = _validator.Text.Trim();
        var staged = SafeDeploy.StagedPath(_remotePath);

        if (template.Length == 0)
        {
            var known = SafeDeploy.ValidatorFor(_remotePath);
            _preview.Text = known is null
                ? (_force.IsChecked == true
                    ? "No validator: this will write the file unchecked."
                    : "No validator is known for this path. Type one (use {file} for the staged file), or tick Force.")
                : "Known validator: " + SafeDeploy.ValidatorCommand(known, staged);
            _deploy.IsEnabled = known is not null || _force.IsChecked == true;
            return;
        }

        _preview.Text = "will run: " + SafeDeploy.ValidatorCommand(template, staged);
        _deploy.IsEnabled = true;
    }

    private async void RunDeploy()
    {
        _deploy.IsEnabled = false;
        _close.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        _result.Text = "Working…";

        try
        {
            var outcome = await DeployRunner.RunAsync(
                _session, _client, _localPath, _remotePath, _validator.Text,
                _force.IsChecked == true, _dryRun.IsChecked == true, 60,
                progress => Dispatcher.Invoke(() =>
                {
                    if (progress.Stage == "uploading" && progress.Total > 0)
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = progress.Sent * 100.0 / progress.Total;
                    }
                    else
                    {
                        _progress.IsIndeterminate = true;
                    }
                }));

            Result = outcome;
            _result.Text = outcome.ToText();
            if (outcome.Ok && !outcome.DryRun) _deploy.Content = "Deploy again";
        }
        catch (Exception ex)
        {
            _result.Text = "deploy failed: " + ex.Message;
        }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
            _deploy.IsEnabled = true;
            _close.IsEnabled = true;
        }
    }
}
