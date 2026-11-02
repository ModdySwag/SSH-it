using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// L9: the live instrument behind the headline. It reports what the app can honestly observe —
/// key to painted echo, frame interval, parser drain, paint cost, memory — and stops sampling when
/// the window is hidden, because an instrument that runs while nobody looks is just load.
/// </summary>
public sealed class PerformanceWindow : Window
{
    private readonly TextBlock _echo = Value();
    private readonly TextBlock _frame = Value();
    private readonly TextBlock _parse = Value();
    private readonly TextBlock _paint = Value();
    private readonly TextBlock _memory = Value();
    private readonly TextBlock _startup = Value();
    private readonly TextBlock _context = Value(12, "Brush.TextMuted");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public PerformanceWindow()
    {
        Title = "Performance";
        Width = 560;
        Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogChrome.Heading("Performance"));
        root.Children.Add(new TextBlock
        {
            Text = "Measured in this window, from real events. Keystroke → echo ends at the paint that " +
                   "shows the echo, not at the photon. Sampling stops while this window is hidden.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        Row(root, "Keystroke → painted echo", _echo);
        Row(root, "Frame interval", _frame);
        Row(root, "Parser drain", _parse);
        Row(root, "Paint (OnRender)", _paint);
        Row(root, "Memory", _memory);
        Row(root, "Startup", _startup);
        root.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10), Opacity = 0.3 });
        root.Children.Add(_context);

        var close = new Button
        {
            Content = "Close", Height = 32, MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;

        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _timer.Start(); } else _timer.Stop();
        };
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
    }

    private static TextBlock Value(double size = 18, string brush = "Brush.Text") => new()
    {
        FontSize = size,
        FontWeight = size >= 18 ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)Application.Current.FindResource(brush),
        Margin = new Thickness(0, 2, 0, 10),
        TextWrapping = TextWrapping.Wrap
    };

    private static void Row(Panel panel, string label, TextBlock value)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        });
        panel.Children.Add(value);
    }

    private void Refresh()
    {
        var s = PerfMetrics.Snapshot();
        _echo.Text = s.EchoCount == 0
            ? "no keystrokes yet — type into a terminal tab while this window is open"
            : $"{s.EchoP50:0.0} ms p50 · {s.EchoP99:0.0} ms p99 · {s.EchoCount} sample(s)";
        _frame.Text = s.FrameCount < 2
            ? "collecting…"
            : $"{s.FrameP50:0.0} ms p50 · {s.FrameP99:0.0} ms p99 · {s.DroppedPercent:0.0}% over 1.5× median";
        _parse.Text = $"{s.ParseMbPerSecond:0.0} MB/s drained · backlog {s.BacklogBytes / 1024.0:0.0} KB";
        _paint.Text = s.RenderP50 == 0 && s.EchoCount == 0
            ? "no paints measured yet"
            : $"{s.RenderP50:0.00} ms p50 · {s.RenderP99:0.00} ms p99";
        _memory.Text = $"{s.WorkingSetMb:0} MB app ({s.PrivateMb:0} MB private)" + Children();
        _startup.Text = s.StartupMs is { } ms ? $"{ms:0} ms (process start → first frame)" : "not measured";

        var dpi = VisualTreeHelper.GetDpi(this);
        _context.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"} · " +
                        $"{Bench.DisplayRefreshHz()} Hz display · {dpi.PixelsPerInchX:0} DPI · " +
                        $"{System.Environment.ProcessorCount} cores";
    }

    private static string Children()
    {
        try
        {
            if (PerfMetrics.Children?.Invoke() is not { Count: > 0 } kids) return "";
            return $" · {kids.Count} ssh child(ren) {kids.Bytes / 1048576.0:0} MB";
        }
        catch { return ""; }
    }
}
