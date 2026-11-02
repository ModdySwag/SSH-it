using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt.Views;

/// <summary>
/// Plays an asciinema v2 (.cast) recording written by the terminal's recorder.
/// Line 1 is the JSON header (width/height/timestamp); every other line is
/// [seconds, "o", "data"]. Seeking rebuilds the screen by replaying from zero,
/// which is cheap because events are just byte chunks.
/// </summary>
public sealed class CastPlayerWindow : Window
{
    private sealed record CastEvent(double Time, string Type, string Data);

    private readonly AppSettings _settings;
    private readonly List<CastEvent> _events = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private TerminalBuffer _buffer = new(80, 24);
    private VtParser _parser;
    private readonly TerminalView _view = new();
    private readonly Slider _timeline;
    private readonly TextBlock _position;
    private readonly TextBlock _info;
    private readonly Button _playButton;
    private readonly ComboBox _speed;
    private readonly ComboBox _blockPicker = new() { Width = 250, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
    private List<CastBlock> _blocks = new();
    private bool _updatingPicker;

    private double _duration;
    private double _clock;
    private int _next;
    private bool _playing;
    private bool _scrubbing;

    public CastPlayerWindow(string path, AppSettings settings)
    {
        _settings = settings;
        _parser = new VtParser(_buffer, _ => { });

        Title = "Recording — " + Path.GetFileName(path);
        Width = 1000;
        Height = 680;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Brush.Text")
        };
        _info = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        header.Children.Add(title);
        header.Children.Add(_info);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var screen = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.TerminalBackground"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6)
        };
        _view.AttachBuffer(_buffer);
        _view.ApplyTheme(TerminalTheme.Get(settings.TerminalTheme));
        _view.ApplyFont(settings.FontFamily, settings.FontSize);
        screen.Child = _view;
        Grid.SetRow(screen, 1);
        root.Children.Add(screen);

        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _playButton = new Button
        {
            Content = Icon("\uE768"),
            Width = 38,
            Height = 30,
            ToolTip = "Play / pause (Space)",
            Style = (Style)Application.Current.FindResource("Btn.Accent")
        };
        _playButton.Click += (_, _) => TogglePlay();
        Grid.SetColumn(_playButton, 0);
        bar.Children.Add(_playButton);

        var restart = new Button
        {
            Content = Icon("\uE72C"),
            Width = 38,
            Height = 30,
            Margin = new Thickness(6, 0, 10, 0),
            ToolTip = "Restart (Home)"
        };
        restart.Click += (_, _) => Seek(0);
        Grid.SetColumn(restart, 1);
        bar.Children.Add(restart);

        _timeline = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = false
        };
        _timeline.ValueChanged += (_, e) => { if (_scrubbing) _position.Text = Clock(e.NewValue) + " / " + Clock(_duration); };
        _timeline.PreviewMouseLeftButtonDown += (_, _) => _scrubbing = true;
        _timeline.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _scrubbing = false;
            Seek(_timeline.Value);
        };
        Grid.SetColumn(_timeline, 2);
        bar.Children.Add(_timeline);

        _position = new TextBlock
        {
            Text = "00:00 / 00:00",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        Grid.SetColumn(_position, 3);
        bar.Children.Add(_position);

        _speed = new ComboBox { Width = 78, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        foreach (var label in new[] { "0.5x", "1x", "2x", "4x" }) _speed.Items.Add(label);
        _speed.SelectedIndex = 1;
        Grid.SetColumn(_speed, 4);
        bar.Children.Add(_speed);

        // R3-15: jump straight to a command block when the recording has an index beside it.
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _blockPicker.VerticalAlignment = VerticalAlignment.Center;
        _blockPicker.ToolTip = "Command blocks in this recording — pick one to jump to it";
        _blockPicker.SelectionChanged += (_, _) =>
        {
            if (_updatingPicker || _blocks.Count == 0) return;
            var index = _blockPicker.SelectedIndex;
            if (index >= 0 && index < _blocks.Count) Seek(_blocks[index].Start);
        };
        Grid.SetColumn(_blockPicker, 5);
        bar.Children.Add(_blockPicker);

        Grid.SetRow(bar, 2);
        root.Children.Add(bar);
        Content = root;

        _timer.Tick += (_, _) => Tick();
        PreviewKeyDown += OnKey;
        Load(path);
    }

    private static TextBlock Icon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private double Speed => _speed.SelectedIndex switch { 0 => 0.5, 2 => 2.0, 3 => 4.0, _ => 1.0 };

    private void Load(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) throw new InvalidOperationException("The recording is empty.");

        int cols = 80, rows = 24;
        try
        {
            using var header = JsonDocument.Parse(lines[0]);
            if (header.RootElement.TryGetProperty("width", out var w)) cols = Math.Max(2, w.GetInt32());
            if (header.RootElement.TryGetProperty("height", out var h)) rows = Math.Max(1, h.GetInt32());
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("This file does not start with an asciinema v2 header.");
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var item = doc.RootElement;
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 3) continue;
                var time = item[0].GetDouble();
                var type = item[1].GetString() ?? "o";
                var data = item[2].GetString() ?? "";
                _events.Add(new CastEvent(time, type, data));
                if (time > _duration) _duration = time;
            }
            catch (JsonException)
            {
                // A truncated final line is normal for a recording that was still open.
            }
        }

        _buffer = new TerminalBuffer(cols, rows) { ScrollbackLimit = _settings.ScrollbackLines };
        _view.AttachBuffer(_buffer);
        _view.ApplyTheme(TerminalTheme.Get(_settings.TerminalTheme));
        _view.ApplyFont(_settings.FontFamily, _settings.FontSize);

        _timeline.Maximum = Math.Max(0.1, _duration);
        _info.Text = $"{cols}×{rows} · {_events.Count} events · {Clock(_duration)} · {FormatSize(new FileInfo(path).Length)}";

        _blocks = CastIndex.Load(path);
        _blockPicker.Items.Clear();
        foreach (var block in _blocks) _blockPicker.Items.Add(block.Label);
        _blockPicker.IsEnabled = _blocks.Count > 0;
        _blockPicker.ToolTip = _blocks.Count > 0
            ? _blocks.Count + " command block(s) — pick one to jump to it"
            : "No block index beside this recording (the shell must emit OSC 133 marks while recording)";
        Seek(0);
        Play();
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? bytes + " B" : (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";

    private static string Clock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>Keeps the picker on the block the playhead is inside, without seeking in a loop.</summary>
    private void SyncPicker()
    {
        if (_blocks.Count == 0) return;
        var block = CastIndex.At(_blocks, _clock);
        var index = block is null ? -1 : _blocks.IndexOf(block);
        if (index == _blockPicker.SelectedIndex) return;
        _updatingPicker = true;
        _blockPicker.SelectedIndex = index;
        _updatingPicker = false;
    }

    private void Tick()
    {
        if (!_playing) return;

        _clock += _timer.Interval.TotalSeconds * Speed;
        var dirty = Drain(_clock);
        if (dirty) _view.RequestRepaint();

        if (_clock >= _duration)
        {
            _clock = _duration;
            Pause();
        }

        _timeline.Value = Math.Clamp(_clock, _timeline.Minimum, _timeline.Maximum);
        _position.Text = Clock(_clock) + " / " + Clock(_duration);
        SyncPicker();
    }

    /// <summary>Feeds every event up to <paramref name="until"/>. Returns true if anything was shown.</summary>
    private bool Drain(double until)
    {
        var fed = false;
        while (_next < _events.Count && _events[_next].Time <= until)
        {
            var ev = _events[_next++];
            if (ev.Type != "o") continue;
            _parser.Feed(Encoding.UTF8.GetBytes(ev.Data));
            fed = true;
        }
        return fed;
    }

    private void Seek(double seconds)
    {
        _clock = Math.Max(0, Math.Min(seconds, _duration));
        _next = 0;

        // Rebuild from scratch so a backwards seek shows exactly what the screen held then.
        _buffer = new TerminalBuffer(_buffer.Columns, _buffer.Rows) { ScrollbackLimit = _settings.ScrollbackLines };
        _parser = new VtParser(_buffer, _ => { });
        _view.AttachBuffer(_buffer);
        _view.ApplyTheme(TerminalTheme.Get(_settings.TerminalTheme));
        _view.ApplyFont(_settings.FontFamily, _settings.FontSize);

        Drain(_clock);
        _view.RequestRepaint();

        _timeline.Value = _clock;
        _position.Text = Clock(_clock) + " / " + Clock(_duration);
        SyncPicker();
    }

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_clock >= _duration) Seek(0);
        _playing = true;
        _playButton.Content = Icon("\uE769");
        _playButton.ToolTip = "Pause (Space)";
        _timer.Start();
    }

    private void Pause()
    {
        _playing = false;
        _playButton.Content = Icon("\uE768");
        _playButton.ToolTip = "Play (Space)";
        _timer.Stop();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Left:
                Seek(_clock - 5);
                e.Handled = true;
                break;
            case Key.Right:
                Seek(_clock + 5);
                e.Handled = true;
                break;
            case Key.Home:
                Seek(0);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
