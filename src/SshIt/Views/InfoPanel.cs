using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// Host information panel: one non-interactive ssh call parsed into fields, plus a
/// live load sparkline sampled every five seconds for remote sessions. Sampling stops
/// whenever the panel is hidden, so an unlooked-at tab costs no ssh round trips.
/// </summary>
public sealed class InfoPanel : UserControl
{
    private readonly MainWindow _owner;
    private readonly StackPanel _content = new();
    private readonly TextBlock _status = new();
    private readonly Button _refresh = new() { Content = "Refresh", Width = 96, Height = 30 };
    private Session? _session;
    private bool _busy;

    private readonly Canvas _spark = new() { Width = 260, Height = 44, ClipToBounds = true };
    private readonly TextBlock _sparkValue = new();
    private readonly List<double> _samples = new();
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _liveBusy;
    private int _emptyReadings;

    private readonly Canvas _host = new() { Width = 260, Height = 44, ClipToBounds = true };
    private readonly TextBlock _hostValue = new();
    private readonly List<double> _cpu = new();
    private readonly List<double> _memory = new();
    private CpuCounter? _lastCpu;

    private readonly WrapPanel _route = new();
    private readonly TextBlock _routeNote = new();
    private readonly Dictionary<string, Border> _routeChips = new(StringComparer.OrdinalIgnoreCase);

    public InfoPanel(MainWindow owner, Session? session)
    {
        _owner = owner;
        SetResourceReference(BackgroundProperty, "Brush.Panel");

        var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
        header.Children.Add(new TextBlock
        {
            Text = "Host information",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        });

        // The jump route is drawn above the host facts: you -> hop -> hop -> target.
        _route.Margin = new Thickness(0, 8, 0, 0);
        _route.Visibility = Visibility.Collapsed;
        _routeNote.FontSize = 11;
        _routeNote.TextWrapping = TextWrapping.Wrap;
        _routeNote.Margin = new Thickness(0, 4, 0, 0);
        _routeNote.Foreground = (Brush)Application.Current.FindResource("Brush.Danger");
        _routeNote.Visibility = Visibility.Collapsed;
        header.Children.Add(_route);
        header.Children.Add(_routeNote);

        _status.Text = "No active session";
        _status.FontSize = 11.5;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _status.Margin = new Thickness(0, 3, 0, 8);
        _status.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_status);

        _refresh.HorizontalAlignment = HorizontalAlignment.Left;
        _refresh.Click += (_, _) => Refresh();
        header.Children.Add(_refresh);
        header.Children.Add(BuildLiveBlock());

        var scroll = new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(14, 4, 8, 12)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroll);
        Content = grid;

        _live.Tick += async (_, _) => await SampleAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) StartLive();
            else _live.Stop();
        };

        SetSession(session);
    }

    public void SetSession(Session? session)
    {
        _session = session;
        _status.Text = session is null
            ? "Select a session to inspect the remote host."
            : $"{session.DisplayTarget} — read with a single non-interactive ssh call.";
        _samples.Clear();
        _cpu.Clear();
        _memory.Clear();
        _lastCpu = null;
        Redraw();
        RedrawHost();
        _content.Children.Clear();
        BuildRoute(session);

        _live.Stop();
        if (session is not null)
        {
            Refresh();
            if (IsVisible) StartLive();
        }
    }

    // ---------------------------------------------------------------- jump route

    /// <summary>
    /// Draws the ProxyJump chain as chips: You -> hop -> hop -> target. Nothing is drawn for a
    /// session with no chain, so the common case stays a plain panel.
    /// </summary>
    private void BuildRoute(Session? session)
    {
        _route.Children.Clear();
        _routeChips.Clear();
        _routeNote.Visibility = Visibility.Collapsed;

        var hops = JumpChain.Hops(session?.JumpHost);
        _route.Visibility = hops.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hops.Count == 0 || session is null) return;

        _route.Children.Add(Chip("You", "you", isTarget: false));
        foreach (var hop in hops)
        {
            _route.Children.Add(Arrow());
            _route.Children.Add(Chip(hop, hop, isTarget: false));
        }

        _route.Children.Add(Arrow());
        _route.Children.Add(Chip(session.DisplayTarget, "target", isTarget: true));
    }

    private Border Chip(string label, string key, bool isTarget)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = isTarget ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource(isTarget ? "Brush.Text" : "Brush.TextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150
        };

        var chip = new Border
        {
            Background = (Brush)Application.Current.FindResource(isTarget ? "Brush.AccentDim" : "Brush.Control"),
            BorderBrush = (Brush)Application.Current.FindResource(isTarget ? "Brush.Accent" : "Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 2, 0, 2),
            Child = text,
            ToolTip = isTarget ? "The host this session connects to" : (key == "you" ? null : "Jump host")
        };

        _routeChips[key] = chip;
        return chip;
    }

    private static TextBlock Arrow() => new()
    {
        Text = "→",
        FontSize = 11,
        Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextDisabled")
    };

    /// <summary>
    /// Marks the hop ssh blamed. When the error names no host the note still appears, but no
    /// chip is marked — saying "somewhere in this chain" is honest, guessing is not.
    /// </summary>
    private void MarkRoute(string? blamed, string? detail)
    {
        var border = (Brush)Application.Current.FindResource("Brush.Border");
        var accent = (Brush)Application.Current.FindResource("Brush.Accent");
        var danger = (Brush)Application.Current.FindResource("Brush.Danger");

        foreach (var (key, chip) in _routeChips)
        {
            var isTarget = key == "target";
            chip.BorderBrush = isTarget ? accent : border;
            chip.BorderThickness = new Thickness(1);
            chip.ToolTip = isTarget ? "The host this session connects to" : (key == "you" ? null : "Jump host");
        }

        if (blamed is not null && _routeChips.TryGetValue(blamed, out var failed))
        {
            failed.BorderBrush = danger;
            failed.BorderThickness = new Thickness(2);
            failed.ToolTip = detail is null ? "This hop failed" : "This hop failed: " + detail;
        }

        _routeNote.Text = detail is null ? "" : (blamed is null ? "Failed somewhere in this chain: " : "Blamed hop is marked: ") + detail;
        _routeNote.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border BuildLiveBlock()
    {
        _sparkValue.FontSize = 11;
        _sparkValue.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _sparkValue.Text = "waiting for a reading…";
        _sparkValue.Margin = new Thickness(0, 4, 0, 0);

        var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = "Load average · every 5 s",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        });
        stack.Children.Add(new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.TerminalBackground"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _spark
        });
        stack.Children.Add(_sparkValue);

        // A second chart for the percentages. Unlike load, 0-100 is the natural axis for both
        // series, so they share one fixed scale and never rescale under the reader.
        _hostValue.FontSize = 11;
        _hostValue.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _hostValue.Text = "waiting for a second sample…";
        _hostValue.Margin = new Thickness(0, 4, 0, 0);

        stack.Children.Add(new TextBlock
        {
            Text = "CPU and memory · every 5 s",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 14, 0, 0)
        });
        stack.Children.Add(new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.TerminalBackground"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _host
        });
        stack.Children.Add(_hostValue);

        return new Border { Child = stack };
    }

    private void StartLive()
    {
        if (_session is null) { _sparkValue.Text = "No session."; _hostValue.Text = "No session."; return; }
        if (_session.IsLocal)
        {
            _sparkValue.Text = "Local shell — nothing remote to sample.";
            _hostValue.Text = "Local shell — nothing remote to sample.";
            return;
        }

        _live.Start();
        _ = SampleAsync();
    }

    /// <summary>
    /// One reading per tick, and one ssh call for all three series: the load line first (so a
    /// host without /proc still yields the uptime fallback), then the CPU counters and the
    /// memory figures. Adding a series must not add a round trip.
    /// </summary>
    private const string SampleCommand =
        "printf 'LOAD='; cut -d' ' -f1-3 /proc/loadavg 2>/dev/null; " +
        "awk 'NR==1 && $1==\"cpu\" {printf \"STAT=\"; for (i=2; i<=9; i++) printf \"%s \", $i; print \"\"}' /proc/stat 2>/dev/null; " +
        "awk '/^MemTotal:/{printf \"MEMTOTAL=%s\\n\", $2} /^MemAvailable:/{printf \"MEMAVAIL=%s\\n\", $2} " +
        "/^MemFree:/{printf \"MEMFREE=%s\\n\", $2} /^Buffers:/{printf \"MEMBUF=%s\\n\", $2} " +
        "/^Cached:/{printf \"MEMCACHED=%s\\n\", $2}' /proc/meminfo 2>/dev/null; " +
        "uptime 2>/dev/null || true";

    /// <summary>One sample: load, CPU counters and memory, in a single non-interactive call.</summary>
    private async Task SampleAsync()
    {
        var session = _session;
        if (session is null || session.IsLocal || _liveBusy) return;

        _liveBusy = true;
        try
        {
            var batch = await Task.Run(() => RunBatchFull(session, SampleCommand));
            var output = batch.Output;

            var load = ParseSample(output);
            if (load is not null)
            {
                _samples.Add(load.Value);
                while (_samples.Count > 48) _samples.RemoveAt(0);
                Redraw();
            }

            // CPU usage needs two counter readings: the first tick only records the baseline.
            var counters = ParseCounters(output);
            if (counters is not null)
            {
                if (_lastCpu is not null && CpuPercent(_lastCpu.Value, counters.Value) is { } percent)
                {
                    _cpu.Add(percent);
                    while (_cpu.Count > 48) _cpu.RemoveAt(0);
                }
                _lastCpu = counters;
            }

            var memory = ParseMemoryPercent(output);
            if (memory is not null)
            {
                _memory.Add(memory.Value);
                while (_memory.Count > 48) _memory.RemoveAt(0);
            }

            RedrawHost();

            if (load is null && counters is null && memory is null)
            {
                // A batch call cannot answer a prompt, so a host with MFA or a password will never
                // answer it no matter how long the panel waits. Say why once and stop asking.
                if (IsInteractiveRefusal(batch.Error))
                {
                    _sparkValue.Text = ExplainNoReading(batch);
                    _hostValue.Text = _sparkValue.Text;
                    _live.Stop();
                    Log.Info("info live stopped: the host wants an interactive prompt");
                }
                else if (++_emptyReadings >= 3)
                {
                    _sparkValue.Text = ExplainNoReading(batch);
                    _hostValue.Text = _sparkValue.Text;
                    _live.Stop();
                    Log.Info("info live stopped: no reading after 3 tries");
                }
                else
                {
                    _sparkValue.Text = "No reading yet — retrying…";
                }
            }
            else
            {
                _emptyReadings = 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("info live: " + ex.Message);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    /// <summary>
    /// Reads the first parseable number. Accepts a bare loadavg line, an uptime line
    /// with "load average:" in front, and a "LOAD=" prefix.
    /// </summary>
    internal static double? ParseSample(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var mark = line.IndexOf("load average", StringComparison.OrdinalIgnoreCase);
            if (mark >= 0)
            {
                var colon = line.IndexOf(':', mark);
                if (colon >= 0) line = line[(colon + 1)..];
            }

            var equals = line.IndexOf("LOAD=", StringComparison.OrdinalIgnoreCase);
            if (equals >= 0) line = line[(equals + 5)..];

            // Only a line made entirely of numbers counts, so prose that happens to
            // contain a date or an address cannot become a sample.
            var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var values = new List<double>();
            var allNumeric = true;
            foreach (var token in tokens)
            {
                if (double.TryParse(token.TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value >= 0 && value < 100000)
                    values.Add(value);
                else
                {
                    allNumeric = false;
                    break;
                }
            }

            if (allNumeric && values.Count > 0) return values[0];
        }

        return null;
    }

    /// <summary>CPU time counters from /proc/stat, in jiffies since boot.</summary>
    internal readonly record struct CpuCounter(ulong Total, ulong Idle);

    /// <summary>
    /// Reads the STAT= line laid down by <see cref="SampleCommand"/>. Same discipline as the
    /// load parser: every token has to be a counter, so a host that prints something else in
    /// that shape cannot become a sample.
    /// </summary>
    internal static CpuCounter? ParseCounters(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("STAT=", StringComparison.Ordinal)) continue;

            var tokens = line[5..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) return null;

            ulong total = 0;
            ulong idle = 0;
            for (var index = 0; index < tokens.Length; index++)
            {
                if (!ulong.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    return null;

                total += value;
                if (index == 3) idle += value;   // idle
                if (index == 4) idle += value;   // iowait is idle time too
            }

            return total == 0 ? null : new CpuCounter(total, idle);
        }

        return null;
    }

    /// <summary>Share of the CPU that was busy between two counter readings.</summary>
    internal static double? CpuPercent(CpuCounter previous, CpuCounter current)
    {
        if (current.Total <= previous.Total) return null;

        var total = current.Total - previous.Total;
        var idle = current.Idle >= previous.Idle ? current.Idle - previous.Idle : 0;
        if (idle > total) return null;

        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    /// <summary>
    /// Memory in use, from MemTotal and MemAvailable. Kernels older than 3.14 have no
    /// MemAvailable, so the free+buffers+cached sum is the fallback rather than no series.
    /// </summary>
    internal static double? ParseMemoryPercent(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        double? total = null, available = null, free = null, buffers = null, cached = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("MEMTOTAL=", StringComparison.Ordinal)) total = ParseNumber(line[9..]);
            else if (line.StartsWith("MEMAVAIL=", StringComparison.Ordinal)) available = ParseNumber(line[9..]);
            else if (line.StartsWith("MEMFREE=", StringComparison.Ordinal)) free = ParseNumber(line[8..]);
            else if (line.StartsWith("MEMBUF=", StringComparison.Ordinal)) buffers = ParseNumber(line[7..]);
            else if (line.StartsWith("MEMCACHED=", StringComparison.Ordinal)) cached = ParseNumber(line[10..]);
        }

        if (total is null || total <= 0) return null;
        if (available is null)
        {
            if (free is null || buffers is null || cached is null) return null;
            available = free + buffers + cached;
        }

        return Math.Clamp((total.Value - available.Value) / total.Value * 100.0, 0, 100);
    }

    private static double? ParseNumber(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;

    private void Redraw()
    {
        _spark.Children.Clear();

        if (_samples.Count == 0)
        {
            _sparkValue.Text = "waiting for a reading…";
            return;
        }

        var width = _spark.Width;
        var height = _spark.Height;
        var peak = Math.Max(1.0, _samples.Max());
        var step = _samples.Count > 1 ? width / (_samples.Count - 1) : 0;

        var line = new Polyline
        {
            Stroke = (Brush)Application.Current.FindResource("Brush.Accent"),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        };

        for (var i = 0; i < _samples.Count; i++)
        {
            var x = _samples.Count > 1 ? i * step : width / 2;
            var y = height - (_samples[i] / peak) * (height - 6) - 3;
            line.Points.Add(new Point(x, y));
        }

        _spark.Children.Add(line);
        _sparkValue.Text = $"{_samples[^1]:0.00} now · peak {peak:0.00} · {_samples.Count} samples";
    }

    /// <summary>
    /// CPU and memory share a fixed 0-100 axis. Load needs an auto-peak because it is
    /// unbounded; these two are percentages, so a fixed scale makes the shapes comparable
    /// between samples instead of rescaling under the reader.
    /// </summary>
    private void RedrawHost()
    {
        _host.Children.Clear();

        if (_cpu.Count == 0 && _memory.Count == 0)
        {
            _hostValue.Text = "waiting for a second sample…";
            return;
        }

        var width = _host.Width;
        var height = _host.Height;

        _host.Children.Add(new Line
        {
            X1 = 0,
            Y1 = height / 2,
            X2 = width,
            Y2 = height / 2,
            Stroke = (Brush)Application.Current.FindResource("Brush.Border"),
            StrokeThickness = 1,
            Opacity = 0.7
        });

        AddSeries(_memory, (Brush)Application.Current.FindResource("Brush.TextMuted"), 1.2);
        AddSeries(_cpu, (Brush)Application.Current.FindResource("Brush.Accent"), 1.6);

        var cpu = _cpu.Count > 0 ? $"{_cpu[^1]:0}%" : "—";
        var memory = _memory.Count > 0 ? $"{_memory[^1]:0}%" : "—";
        var samples = Math.Max(_cpu.Count, _memory.Count);
        _hostValue.Text = $"CPU {cpu} · memory {memory} · {samples} sample" + (samples == 1 ? "" : "s");
    }

    private void AddSeries(List<double> samples, Brush stroke, double thickness)
    {
        if (samples.Count == 0) return;

        var width = _host.Width;
        var height = _host.Height;
        var step = samples.Count > 1 ? width / (samples.Count - 1) : 0;

        var line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round
        };

        for (var index = 0; index < samples.Count; index++)
        {
            var x = samples.Count > 1 ? index * step : width / 2;
            var y = height - (samples[index] / 100.0) * (height - 6) - 3;
            line.Points.Add(new Point(x, y));
        }

        _host.Children.Add(line);
    }

    private async void Refresh()
    {
        if (_session is null || _busy) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _status.Text = "Reading " + _session.DisplayTarget + "…";
        _content.Children.Clear();

        var command =
            "printf 'OS='; (grep -m1 PRETTY_NAME /etc/os-release 2>/dev/null | cut -d= -f2- | tr -d '\\\"') || echo unknown; " +
            "printf 'KERNEL='; uname -r; " +
            "printf 'UPTIME='; uptime -p 2>/dev/null || uptime; " +
            "printf 'LOAD='; cut -d' ' -f1-3 /proc/loadavg 2>/dev/null; " +
            "printf 'CPU='; nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || echo ?; " +
            "printf 'MEM='; free -m 2>/dev/null | awk 'NR==2{print $3\" used of \"$2\" MB\"}'; " +
            "printf 'DISK='; df -h / 2>/dev/null | awk 'NR==2{print $3\" used of \"$2\" (\"$5\")\"}'; " +
            "printf 'USER='; whoami; " +
            "printf 'HOME='; printf '%s' \"$HOME\";";

        var result = await Task.Run(() => RunBatchFull(_session, command));
        _busy = false;
        _refresh.IsEnabled = true;

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            var error = JumpChain.FirstLine(result.Error);
            _status.Text = error is null
                ? "No response. The host may be unreachable, or it does not allow non-interactive commands."
                : "No response: " + error;

            // ssh names the host it could not reach; that is what the route diagram marks.
            MarkRoute(JumpChain.FailingHop(result.Error, JumpChain.Hops(_session.JumpHost), _session.DisplayTarget), error);
            return;
        }

        MarkRoute(null, null);
        _status.Text = _session.DisplayTarget;
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.TrimEnd();
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            AddRow(line[..equals], line[(equals + 1)..]);
        }
    }

    private void AddRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 7, 0, 7),
            VerticalAlignment = VerticalAlignment.Top
        };
        var valueBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 6)
        };

        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        _content.Children.Add(row);
        _content.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.FindResource("Brush.Border"),
            Opacity = 0.6
        });
    }

    internal sealed record BatchResult(string Output, string Error, int ExitCode);

    /// <summary>
    /// Quotes the remote command so ssh.exe receives it as ONE argument. Appended raw, the
    /// Windows command-line parser strips the command's own double quotes before ssh ever sees
    /// them, which silently breaks any awk/printf quoting: the load line survives (no quotes)
    /// while the CPU and memory lines come back as awk syntax errors.
    /// </summary>
    private static string QuoteCommand(string value) => SshLauncher.QuoteArgument(value);

    /// <summary>True when ssh refused because the host wants a prompt the sampler cannot answer.</summary>
    internal static bool IsInteractiveRefusal(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("keyboard-interactive", StringComparison.OrdinalIgnoreCase)
            || error.Contains("verification code", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Enter PASSCODE", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Permission denied (password", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sentence the panel shows instead of an endless "waiting for a reading…".</summary>
    internal static string ExplainNoReading(BatchResult result)
    {
        if (IsInteractiveRefusal(result.Error))
            return "The host wants an interactive prompt (MFA or a password), so the live panel cannot read it - open a pane to this host instead.";

        var error = (result.Error ?? "").Trim();
        var cut = error.LastIndexOf((char)10);
        var tail = cut >= 0 ? error[(cut + 1)..].Trim() : error;
        if (tail.Length > 90) tail = tail[..90] + "...";
        return tail.Length > 0 ? "No reading from the host: " + tail : "No reading from the host.";
    }

    internal static string RunBatch(Session session, string command) => RunBatchFull(session, command).Output;

    internal static BatchResult RunBatchFull(Session session, string command)
    {
        try
        {
            var args = SshLauncher.BuildBatchArguments(session) + " " + QuoteCommand(command);
            var info = new ProcessStartInfo(SshLauncher.SshExe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            var secret = CredentialVault.GetSessionSecret(session.Id);
            if (!string.IsNullOrEmpty(secret))
            {
                info.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? "";
                info.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                info.Environment["CONDUIT_ASKPASS"] = CredentialVault.StoreAskPass(session.Id, secret);
            }

            using var process = Process.Start(info);
            if (process is null) return new BatchResult("", "", -1);

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(12000);
            return new BatchResult(output, error, process.HasExited ? process.ExitCode : -1);
        }
        catch (Exception ex)
        {
            Log.Warn("info: " + ex.Message);
            return new BatchResult("", ex.Message, -1);
        }
    }
}
