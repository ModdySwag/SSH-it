using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Terminal;

/// <summary>
/// Terminal renderer. Cells are painted into per-line drawing groups that are
/// cached by content version, so typing repaints one line, not the screen.
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    private static readonly Typeface Fallback = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly Dictionary<long, CachedRow> _cache = new();
    private readonly DispatcherTimerBridge _blink;
    private readonly Cursor _ibeam = Cursors.IBeam;

    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _baselineOffset;
    private bool _metricsValid;
    private bool _cursorOn = true;
    private long _topLine;
    private int _scrollOffset;
    private int _themeStamp;
    private int _fontStamp;
    private int _rulesStamp;
    private IReadOnlyList<HighlightRule> _rules = Array.Empty<HighlightRule>();
    private Point _dragAnchor;
    private bool _dragging;
    private bool _mouseDown;
    private Point _lastMouseCell;

    public TerminalBuffer Buffer { get; private set; } = new(80, 24);
    public TerminalTheme Theme { get; private set; } = TerminalTheme.BuiltIn[0];
    public string FontFamilyName { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 13;

    /// <summary>Raised with raw bytes that must be written to the remote shell.</summary>
    public event Action<byte[]>? Input;

    /// <summary>Raised when the visible grid size changes, so the pty can be resized.</summary>
    public event Action<int, int>? GridSizeChanged;

    /// <summary>Raised when the user clicks a link-like token while holding Ctrl.</summary>
    public event Action<string>? LinkActivated;

    public bool IsConnected { get; set; }

    /// <summary>Set by the host when a search is active.</summary>
    public string SearchText { get; private set; } = "";
    public bool SearchCaseSensitive { get; set; }
    private readonly List<(long line, int start, int length)> _matches = new();

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = _ibeam;
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _blink = new DispatcherTimerBridge(TimeSpan.FromMilliseconds(530), () =>
        {
            _cursorOn = !_cursorOn;
            if (Buffer.CursorBlink) InvalidateRow(Buffer.CursorRow);
        });

        Buffer.Damaged += OnBufferDamaged;
        Buffer.TitleChanged += OnTitleChanged;
        Unloaded += (_, _) => _blink.Stop();
        Loaded += (_, _) => { if (IsConnected) _blink.Start(); };
    }

    private readonly Queue<Action> _pending = new();

    private void OnBufferDamaged() => RequestRepaint();

    private void OnTitleChanged(string title) => TitleChanged?.Invoke(title);

    public event Action<string>? TitleChanged;

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (connected) _blink.Start(); else _blink.Stop();
        InvalidateVisual();
    }

    private TerminalAutomationPeer? _automationPeer;
    private DateTime _lastTextEvent = DateTime.MinValue;

    /// <summary>Creates the accessibility peer; without it a screen reader sees an empty box.</summary>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        _automationPeer = new TerminalAutomationPeer(this);
        return _automationPeer;
    }

    /// <summary>Announces new output to assistive tech, at most once every 600 ms.</summary>
    private void NotifyAccessibility()
    {
        if (_automationPeer is null) return;
        if ((DateTime.UtcNow - _lastTextEvent).TotalMilliseconds < 600) return;
        _lastTextEvent = DateTime.UtcNow;
        _automationPeer.NotifyTextChanged();
    }

    /// <summary>Raised when the visual selection changes, so the automation peer can announce it.</summary>
    public event Action? SelectionChanged;

    private void RaiseSelectionChanged()
    {
        try { SelectionChanged?.Invoke(); }
        catch { /* a listener must never break input */ }
    }

    /// <summary>Coalesces high-frequency output into one repaint per frame.</summary>
    public void RequestRepaint()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RequestRepaint);
            return;
        }
        if (_pending.Count > 0) return;
        _pending.Enqueue(() => { _pending.Clear(); InvalidateVisual(); NotifyAccessibility(); });
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, _pending.Peek());
    }

    // ---------------------------------------------------------------- setup

    public void AttachBuffer(TerminalBuffer buffer)
    {
        Buffer.Damaged -= OnBufferDamaged;
        Buffer.TitleChanged -= OnTitleChanged;
        Buffer = buffer;
        Buffer.Damaged += OnBufferDamaged;
        Buffer.TitleChanged += OnTitleChanged;
        _cache.Clear();
        _scrollOffset = 0;
        _metricsValid = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void ApplyTheme(TerminalTheme theme)
    {
        Theme = theme;
        _themeStamp++;
        _cache.Clear();
        InvalidateVisual();
    }

    /// <summary>Keyword-highlighting rules. Changing them invalidates every cached row.</summary>
    public void ApplyHighlights(IReadOnlyList<HighlightRule>? rules)
    {
        _rules = rules ?? Array.Empty<HighlightRule>();
        _rulesStamp++;
        _cache.Clear();
        InvalidateVisual();
    }

    public void ApplyFont(string family, double size)
    {
        FontFamilyName = family;
        FontSize = size;
        _fontStamp++;
        _metricsValid = false;
        _cache.Clear();
        InvalidateMeasure();
        InvalidateVisual();
        UpdateGrid();
    }

    public int Columns => Math.Max(2, (int)Math.Floor(ActualWidth / Math.Max(1, _cellWidth)));
    public int Rows => Math.Max(1, (int)Math.Floor(ActualHeight / Math.Max(1, _cellHeight)));

    private void EnsureMetrics()
    {
        if (_metricsValid) return;

        var typeface = new Typeface(new FontFamily(FontFamilyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var sample = new FormattedText("MMMMMMMMMMMMMMMMMMMM", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _cellWidth = Math.Max(4, sample.WidthIncludingTrailingWhitespace / 20.0);

        var heightSample = new FormattedText("Mg", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _cellHeight = Math.Ceiling(heightSample.Height);
        _baselineOffset = Math.Round((_cellHeight - heightSample.Height) / 2);
        _metricsValid = true;
        Services.Log.Info($"metrics: font={FontFamilyName} size={FontSize} cell={_cellWidth:0.00}x{_cellHeight:0.00} sampleH={heightSample.Height:0.00} dpi={VisualTreeHelper.GetDpi(this).PixelsPerDip:0.00}");
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        return new Size(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        EnsureMetrics();
        UpdateGrid();
    }

    private int _lastCols = -1, _lastRows = -1;
    private int _logRows = -1, _logCols = -1;

    private void UpdateGrid()
    {
        var cols = Columns;
        var rows = Rows;
        if (cols == _lastCols && rows == _lastRows) return;
        _lastCols = cols;
        _lastRows = rows;
        Buffer.Resize(cols, rows);
        GridSizeChanged?.Invoke(cols, rows);
        _cache.Clear();
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- rendering

    protected override void OnRender(DrawingContext dc)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        OnRenderCore(dc);
        watch.Stop();
        Services.PerfMetrics.Rendered(watch.Elapsed.TotalMilliseconds);
    }

    private void OnRenderCore(DrawingContext dc)
    {
        EnsureMetrics();

        var dpi = VisualTreeHelper.GetDpi(this);
        var background = new SolidColorBrush(Theme.Background);
        background.Freeze();
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var rows = Rows;
        var cols = Columns;
        if (rows != _logRows || cols != _logCols)
        {
            _logRows = rows; _logCols = cols;
            Services.Log.Info($"grid: {cols}x{rows} view={ActualWidth:0}x{ActualHeight:0} cell={_cellWidth:0.00}x{_cellHeight:0.00} buffer={Buffer.Columns}x{Buffer.Rows}");
        }
        _topLine = Buffer.Scrollback.Count - _scrollOffset;

        DrawPromptRows(dc, _topLine, rows);

        for (var row = 0; row < rows; row++)
        {
            var global = _topLine + row;
            if (global < 0 || global >= Buffer.TotalLines) continue;
            var line = Buffer.LineAtAbsolute(global);
            if (line is null) continue;

            var y = row * _cellHeight;
            var group = GetRowDrawing(global, line, cols);
            if (group is not null)
            {
                // Row drawings are cached in row-local coordinates; move them into place.
                dc.PushTransform(new TranslateTransform(0, y));
                dc.DrawDrawing(group);
                dc.Pop();
            }
        }

        DrawPromptStatus(dc, _topLine, rows);
        DrawSelection(dc);
        DrawSearchHits(dc);
        DrawCursor(dc);
        DrawScrollIndicator(dc);
    }


    // ---------------------------------------------------------------- command blocks

    /// <summary>
    /// Prompt rows get a barely-there wash, so a long transcript has visible structure without
    /// taking a single column away from the grid. The exit dot sits at the right edge.
    /// </summary>
    private void DrawPromptRows(DrawingContext dc, long top, int rows)
    {
        if (Buffer.AltActive) return;
        if (Buffer.Prompts.Count == 0) return;

        var tint = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
        tint.Freeze();
        foreach (var mark in Buffer.Prompts)
        {
            if (mark.Line < top || mark.Line >= top + rows) continue;
            var y = (mark.Line - top) * _cellHeight;
            dc.DrawRectangle(tint, null, new Rect(0, y, ActualWidth, _cellHeight));
        }
    }

    private void DrawPromptStatus(DrawingContext dc, long top, int rows)
    {
        // A full-screen app owns the whole surface; block chrome from the primary buffer must not
        // be painted over it (the row numbers refer to scrollback that is not on screen).
        if (Buffer.AltActive) return;
        if (Buffer.Prompts.Count == 0) return;

        var size = Math.Max(4, _cellHeight * 0.22);
        foreach (var mark in Buffer.Prompts)
        {
            if (mark.Line < top || mark.Line >= top + rows) continue;
            if (mark.ExitCode < 0) continue;

            var colour = mark.ExitCode == 0
                ? Color.FromArgb(170, 90, 200, 130)
                : Color.FromArgb(190, 220, 90, 90);
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            var centre = new Point(ActualWidth - size, (mark.Line - top) * _cellHeight + _cellHeight / 2);
            dc.DrawEllipse(brush, null, centre, size / 2, size / 2);
        }
    }

    /// <summary>Steps to the previous (-1) or next (+1) command block and scrolls it to the top.</summary>
    public void JumpToPrompt(int direction)
    {
        if (Buffer.Prompts.Count == 0) return;

        var top = TopVisibleLine;
        PromptMark? best = null;
        foreach (var mark in Buffer.Prompts)
        {
            var beyond = direction < 0 ? mark.Line < top - 1 : mark.Line > top + 1;
            if (!beyond) continue;
            if (best is null || (direction < 0 ? mark.Line > best.Line : mark.Line < best.Line)) best = mark;
        }
        if (best is not null) ScrollToLine(best.Line);
    }

    private DrawingGroup? GetRowDrawing(long global, TermLine line, int cols)
    {
        if (_cache.TryGetValue(global, out var cached) &&
            cached.Version == line.Version &&
            cached.Theme == _themeStamp &&
            cached.Font == _fontStamp &&
            Math.Abs(cached.Width - ActualWidth) < 0.5)
        {
            return cached.Group;
        }

        if (_cache.Count > 600) _cache.Clear();

        var group = BuildRow(line, cols);
        _cache[global] = new CachedRow
        {
            Version = line.Version,
            Theme = _themeStamp,
            Font = _fontStamp,
            Width = ActualWidth,
            Group = group
        };
        return group;
    }

    private DrawingGroup BuildRow(TermLine line, int cols)
    {
        var group = new DrawingGroup();
        var dc = group.Open();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // A resized buffer can still hold lines created at the old width.
        var cells = Math.Min(cols, line.Cells.Length);

        // Keyword highlighting needs the row's text once, plus the cell each character starts at.
        var cellOfChar = Array.Empty<int>();
        IReadOnlyList<Highlighter.Span> spans = Array.Empty<Highlighter.Span>();
        if (_rules.Count > 0 && cells > 0)
        {
            var rowText = BuildRowText(line, cells, out cellOfChar);
            spans = Highlighter.Spans(rowText, _rules);
        }

        var x = 0;
        while (x < cells)
        {
            var (_, bg) = Resolve(line.Cells[x]);
            var runStart = x;
            x++;
            while (x < cells)
            {
                var (_, next) = Resolve(line.Cells[x]);
                if (!next.Equals(bg)) break;
                x++;
            }
            var runEnd = Math.Min(x, cells);
            if (!bg.Equals(Theme.Background))
            {
                var brush = new SolidColorBrush(bg);
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(runStart * _cellWidth, 0,
                    (runEnd - runStart) * _cellWidth, _cellHeight));
            }
        }

        // Highlight washes sit above the cell backgrounds and below the glyphs, so a rule's
        // colour reads even when the shell painted the row with its own background.
        foreach (var span in spans)
        {
            var wash = Highlighter.ParseColour(span.Rule.Background);
            if (wash is null || cellOfChar.Length == 0) continue;
            var first = cellOfChar[Math.Clamp(span.Start, 0, cellOfChar.Length - 1)];
            var lastChar = Math.Clamp(span.Start + span.Length - 1, 0, cellOfChar.Length - 1);
            var last = cellOfChar[lastChar];
            var widthCells = last - first + Math.Max(1, (int)line.Cells[last].Width);
            var brush = new SolidColorBrush(wash.Value);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(first * _cellWidth, 0,
                widthCells * _cellWidth, _cellHeight));
        }

        // Text runs: same foreground + same attributes.
        x = 0;
        while (x < cells)
        {
            var cell = line.Cells[x];
            if (cell.Width == 0) { x++; continue; }
            if (cell.Codepoint <= 0 || cell.Codepoint == ' ')
            {
                if (cell.Codepoint == ' ' && (cell.Flags & CellFlags.Underline) == 0) { x += Math.Max(1, (int)cell.Width); continue; }
            }

            var start = x;
            var flags = cell.Flags;
            var fgPacked = cell.Fg;
            var builder = new StringBuilder();
            while (x < cells)
            {
                var c = line.Cells[x];
                if (c.Width == 0) { x++; continue; }
                if (c.Flags != flags || c.Fg != fgPacked) break;
                builder.Append(c.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(c.Codepoint));
                x += Math.Max(1, (int)c.Width);
            }

            var text = builder.ToString();
            if (text.Trim().Length == 0 && (flags & CellFlags.Underline) == 0) continue;

            var (fg, _) = Resolve(cell);
            if ((cell.Flags & CellFlags.Hidden) != 0) fg = Theme.Background;

            var brush = new SolidColorBrush(fg);
            brush.Freeze();

            var typeface = new Typeface(new FontFamily(FontFamilyName),
                (flags & CellFlags.Italic) != 0 ? FontStyles.Italic : FontStyles.Normal,
                (flags & CellFlags.Bold) != 0 ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, FontSize, brush, dpi)
            {
                Trimming = TextTrimming.None,
                TextAlignment = TextAlignment.Left
            };

            if ((flags & CellFlags.Underline) != 0)
                ft.SetTextDecorations(TextDecorations.Underline);
            if ((flags & CellFlags.Strike) != 0)
                ft.SetTextDecorations(TextDecorations.Strikethrough);

            // Snap to the grid; if the shaper disagrees by more than half a cell
            // on a long run, draw character by character so columns stay aligned.
            var expected = (x - start) * _cellWidth;
            if (Math.Abs(ft.WidthIncludingTrailingWhitespace - expected) > _cellWidth * 1.5 && text.Length > 1)
            {
                var cx = start;
                foreach (var ch in text)
                {
                    var one = new FormattedText(ch.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        typeface, FontSize, brush, dpi);
                    dc.DrawText(one, new Point(cx * _cellWidth, _baselineOffset));
                    cx += Math.Max(1, TerminalBuffer.CharWidth(ch));
                }
            }
            else
            {
                dc.DrawText(ft, new Point(start * _cellWidth, _baselineOffset));
            }
        }

        // Re-draw matched glyphs in the rule's colour (and weight). A second pass over the few
        // matched characters is far cheaper than splitting every text run at a highlight edge.
        foreach (var span in spans)
        {
            if (cellOfChar.Length == 0) continue;
            var colour = Highlighter.ParseColour(span.Rule.Foreground);
            if (colour is null && !span.Rule.Bold) continue;
            var firstChar = Math.Clamp(span.Start, 0, cellOfChar.Length - 1);
            var lastChar = Math.Clamp(span.Start + span.Length - 1, 0, cellOfChar.Length - 1);
            for (var i = firstChar; i <= lastChar; i++)
            {
                var cellIndex = cellOfChar[i];
                if (cellIndex < 0 || cellIndex >= cells) continue;
                var cell = line.Cells[cellIndex];
                if (cell.Width == 0 || cell.Codepoint <= 0) continue;

                var fg = colour ?? Resolve(cell).fg;
                var brush = new SolidColorBrush(fg);
                brush.Freeze();
                var typeface = new Typeface(new FontFamily(FontFamilyName),
                    (cell.Flags & CellFlags.Italic) != 0 ? FontStyles.Italic : FontStyles.Normal,
                    span.Rule.Bold || (cell.Flags & CellFlags.Bold) != 0 ? FontWeights.Bold : FontWeights.Normal,
                    FontStretches.Normal);
                var ft = new FormattedText(char.ConvertFromUtf32(cell.Codepoint), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, FontSize, brush, dpi);
                dc.DrawText(ft, new Point(cellIndex * _cellWidth, _baselineOffset));
            }
        }

        dc.Close();
        group.Freeze();
        return group;
    }

    /// <summary>The row as text, with a map from character index to the cell it starts in.</summary>
    private static string BuildRowText(TermLine line, int cells, out int[] cellOfChar)
    {
        var sb = new StringBuilder(cells);
        var map = new List<int>(cells);
        for (var x = 0; x < cells; x++)
        {
            var cell = line.Cells[x];
            if (cell.Width == 0) continue;
            var text = cell.Codepoint <= 0 ? " " : char.ConvertFromUtf32(cell.Codepoint);
            foreach (var _ in text) map.Add(x);
            sb.Append(text);
        }
        cellOfChar = map.ToArray();
        return sb.ToString();
    }

    private (Color fg, Color bg) Resolve(in Cell cell)
    {
        var fg = Theme.Resolve(cell.Fg, true);
        var bg = Theme.Resolve(cell.Bg, false);
        if ((cell.Flags & CellFlags.Reverse) != 0) (fg, bg) = (bg, fg);
        if ((cell.Flags & CellFlags.Dim) != 0) fg = Blend(fg, bg, 0.55);
        return (fg, bg);
    }

    private static Color Blend(Color fg, Color bg, double amount)
        => Color.FromRgb(
            (byte)(bg.R + (fg.R - bg.R) * amount),
            (byte)(bg.G + (fg.G - bg.G) * amount),
            (byte)(bg.B + (fg.B - bg.B) * amount));

    private void DrawSelection(DrawingContext dc)
    {
        if (!HasSelection) return;

        var segments = SelectionSegments().ToList();
        if (segments.Count == 0) return;

        if (Theme.OutlinedHighlights)
        {
            var pen = new Pen(new SolidColorBrush(Theme.Highlight), 2);
            pen.Freeze();
            foreach (var (row, start, end) in segments)
            {
                var y = row * _cellHeight;
                dc.DrawRectangle(null, pen,
                    new Rect(start * _cellWidth, y, (end - start + 1) * _cellWidth, _cellHeight));
            }
            return;
        }

        var brush = new SolidColorBrush(Color.FromArgb(90, Theme.Selection.R, Theme.Selection.G, Theme.Selection.B));
        brush.Freeze();

        foreach (var (row, start, end) in segments)
        {
            var y = row * _cellHeight;
            dc.DrawRectangle(brush, null, new Rect(start * _cellWidth, y, (end - start + 1) * _cellWidth, _cellHeight));
        }
    }

    private void DrawSearchHits(DrawingContext dc)
    {
        if (_matches.Count == 0) return;

        Brush brush;
        Pen? pen = null;
        if (Theme.OutlinedHighlights)
        {
            brush = Brushes.Transparent;
            pen = new Pen(new SolidColorBrush(Theme.Highlight), 2);
            pen.Freeze();
        }
        else
        {
            brush = new SolidColorBrush(Color.FromArgb(100, Theme.Highlight.R, Theme.Highlight.G, Theme.Highlight.B));
            brush.Freeze();
        }

        var rows = Rows;
        foreach (var (line, start, length) in _matches)
        {
            var row = line - _topLine;
            if (row < 0 || row >= rows) continue;
            dc.DrawRectangle(brush, pen, new Rect(start * _cellWidth, row * _cellHeight, length * _cellWidth, _cellHeight));
        }
    }

    private void DrawCursor(DrawingContext dc)
    {
        if (!IsConnected || !Buffer.CursorVisible || _scrollOffset != 0) return;
        if (!IsFocused) return;
        if (!_cursorOn && Buffer.CursorBlink) return;

        var row = Buffer.CursorRow;
        var col = Buffer.CursorCol;
        if (row < 0 || row >= Rows || col < 0 || col >= Columns) return;

        var brush = new SolidColorBrush(Theme.Cursor) { Opacity = 0.85 };
        brush.Freeze();
        var x = col * _cellWidth;
        var y = row * _cellHeight;

        switch (Buffer.CursorShape)
        {
            case 1:
                dc.DrawRectangle(brush, null, new Rect(x, y, Math.Max(1.5, _cellWidth / 8), _cellHeight));
                break;
            case 2:
                dc.DrawRectangle(brush, null, new Rect(x, y + _cellHeight - Math.Max(2, _cellHeight / 10), _cellWidth, Math.Max(2, _cellHeight / 10)));
                break;
            default:
                dc.DrawRectangle(brush, null, new Rect(x, y, _cellWidth, _cellHeight));
                break;
        }
    }

    private void DrawScrollIndicator(DrawingContext dc)
    {
        if (_scrollOffset <= 0) return;
        var brush = new SolidColorBrush(Color.FromArgb(140, Theme.Cursor.R, Theme.Cursor.G, Theme.Cursor.B));
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(ActualWidth - 4, 0, 4, ActualHeight));
    }

    private void InvalidateRow(int row)
    {
        if (row < 0 || row >= Buffer.Rows) return;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- selection

    public bool HasSelection => SelectionStart.Line >= 0 && !SelectionStart.Equals(SelectionEnd);
    public (long Line, int Column) SelectionStart { get; private set; } = (-1, 0);
    public (long Line, int Column) SelectionEnd { get; private set; } = (-1, 0);

    private (long, int) OrderedStart => SelectionStart.Line < SelectionEnd.Line ||
                                        SelectionStart.Line == SelectionEnd.Line && SelectionStart.Column <= SelectionEnd.Column
        ? SelectionStart : SelectionEnd;

    private (long, int) OrderedEnd => OrderedStart.Equals(SelectionStart) ? SelectionEnd : SelectionStart;

    private IEnumerable<(int row, int start, int end)> SelectionSegments()
    {
        var (sLine, sCol) = OrderedStart;
        var (eLine, eCol) = OrderedEnd;
        if (sLine < 0) yield break;

        for (var g = sLine; g <= eLine; g++)
        {
            var row = (int)(g - _topLine);
            if (row < 0 || row >= Rows) continue;
            var start = g == sLine ? sCol : 0;
            var end = g == eLine ? eCol : Math.Max(0, Buffer.Columns - 1);
            yield return (row, start, end);
        }
    }

    public string GetSelectionText()
    {
        if (!HasSelection) return "";
        var (sLine, sCol) = OrderedStart;
        var (eLine, eCol) = OrderedEnd;
        var builder = new StringBuilder();

        for (var g = sLine; g <= eLine; g++)
        {
            var line = Buffer.LineAtAbsolute(g);
            if (line is null) continue;
            var start = g == sLine ? sCol : 0;
            var end = g == eLine ? eCol : Buffer.Columns - 1;
            start = Math.Clamp(start, 0, Buffer.Columns - 1);
            end = Math.Clamp(end, 0, Buffer.Columns - 1);

            var sb = new StringBuilder();
            for (var c = start; c <= end; c++)
            {
                var cell = line.Cells[c];
                if (cell.Width == 0) continue;
                sb.Append(cell.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
            }
            builder.Append(sb.ToString().TrimEnd());
            if (g != eLine) builder.AppendLine();
        }
        return builder.ToString();
    }

    public void ClearSelection()
    {
        SelectionStart = (-1, 0);
        SelectionEnd = (-1, 0);
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    public void SelectAll()
    {
        SelectionStart = (0, 0);
        SelectionEnd = (Buffer.TotalLines - 1, Buffer.Columns - 1);
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    // ---------------------------------------------------------------- automation geometry
    // The accessibility peer works in character offsets over the text it publishes, which is
    // the viewport it is drawing. These members are the bridge between that text and the
    // buffer's cells, so a range can be highlighted exactly where it is read.

    /// <summary>Absolute line number of the top row the view is showing.</summary>
    public long TopVisibleLine => Buffer.Scrollback.Count - _scrollOffset;

    public double CellWidth => _cellWidth;

    public double CellHeight => _cellHeight;

    /// <summary>Sets the visual selection from absolute line/column pairs; the end column is inclusive.</summary>
    public void SetSelection(long startLine, int startColumn, long endLine, int endColumn)
    {
        var lastLine = Math.Max(0, Buffer.TotalLines - 1);
        var lastColumn = Math.Max(0, Buffer.Columns - 1);
        SelectionStart = (Math.Clamp(startLine, 0, lastLine), Math.Clamp(startColumn, 0, lastColumn));
        SelectionEnd = (Math.Clamp(endLine, 0, lastLine), Math.Clamp(endColumn, 0, lastColumn));
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    // ---------------------------------------------------------------- search

    public int Search(string text, bool caseSensitive, bool backwards = false, bool fromTop = false)
    {
        SearchText = text;
        SearchCaseSensitive = caseSensitive;
        _matches.Clear();
        if (string.IsNullOrEmpty(text)) { InvalidateVisual(); return 0; }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var g = 0L; g < Buffer.TotalLines; g++)
        {
            var line = Buffer.LineAtAbsolute(g);
            if (line is null) continue;
            var builder = new StringBuilder();
            foreach (var cell in line.Cells)
            {
                if (cell.Width == 0) continue;
                builder.Append(cell.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
            }
            var haystack = builder.ToString();
            var index = 0;
            while (index <= haystack.Length - text.Length)
            {
                var found = haystack.IndexOf(text, index, comparison);
                if (found < 0) break;
                _matches.Add((g, found, text.Length));
                index = found + Math.Max(1, text.Length);
            }
        }

        InvalidateVisual();
        return _matches.Count;
    }

    public bool FindNext(bool backwards)
    {
        if (_matches.Count == 0) return false;
        var current = SelectionStart.Line;
        var index = backwards
            ? _matches.FindLastIndex(m => m.line < current)
            : _matches.FindIndex(m => m.line > current);
        if (index < 0) index = backwards ? _matches.Count - 1 : 0;
        var match = _matches[index];
        SelectionStart = (match.line, match.start);
        SelectionEnd = (match.line, match.start + match.length - 1);
        CurrentMatch = index + 1;
        ScrollToLine(match.line);
        InvalidateVisual();
        RaiseSelectionChanged();
        return true;
    }

    public int MatchCount => _matches.Count;
    public int CurrentMatch { get; private set; }

    public void ClearSearch()
    {
        SearchText = "";
        _matches.Clear();
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- scrolling

    public void ScrollToLine(long globalLine)
    {
        var maxOffset = Buffer.Scrollback.Count;
        var desiredTop = globalLine - Buffer.Rows / 2;
        var offset = (int)(Buffer.Scrollback.Count - desiredTop);
        _scrollOffset = Math.Clamp(offset, 0, maxOffset);
        InvalidateVisual();
    }

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        InvalidateVisual();
    }

    public void ScrollLines(int lines)
    {
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, Buffer.Scrollback.Count);
        InvalidateVisual();
    }

    public bool IsScrolledBack => _scrollOffset > 0;

    // ---------------------------------------------------------------- input

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        SendText(e.Text);
        e.Handled = true;
    }

    private void SendText(string text)
    {
        if (Buffer.BracketedPaste && text.Length > 1)
            text = "\u001b[200~" + text + "\u001b[201~";
        Input?.Invoke(Encoding.UTF8.GetBytes(text));
    }

    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r");
        if (Buffer.BracketedPaste)
            Input?.Invoke(Encoding.UTF8.GetBytes("\u001b[200~" + text + "\u001b[201~"));
        else
            Input?.Invoke(Encoding.UTF8.GetBytes(text));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // Clipboard and view shortcuts are handled by the host so they can be
        // routed through the menu as well; only raw key encoding lives here.
        if (ctrl && shift && (e.Key == Key.C || e.Key == Key.V)) return;

        if (e.Key is >= Key.F1 and <= Key.F12)
        {
            SendEscape(FunctionKey(e.Key));
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter: Send("\r"); e.Handled = true; return;
            case Key.Back: Send("\u007f"); e.Handled = true; return;
            case Key.Tab: Send(shift ? "\u001b[Z" : "\t"); e.Handled = true; return;
            case Key.Escape: Send("\u001b"); e.Handled = true; return;
            case Key.Up: Send(CursorKey("A")); e.Handled = true; return;
            case Key.Down: Send(CursorKey("B")); e.Handled = true; return;
            case Key.Right: Send(CursorKey("C")); e.Handled = true; return;
            case Key.Left: Send(CursorKey("D")); e.Handled = true; return;
            case Key.Home: Send(CursorKey("H")); e.Handled = true; return;
            case Key.End: Send(CursorKey("F")); e.Handled = true; return;
            case Key.Insert: Send("\u001b[2~"); e.Handled = true; return;
            case Key.Delete: Send("\u001b[3~"); e.Handled = true; return;
            case Key.PageUp:
                if (Buffer.MouseMode == 0) { ScrollLines(Rows - 2); e.Handled = true; return; }
                Send("\u001b[5~"); e.Handled = true; return;
            case Key.PageDown:
                if (Buffer.MouseMode == 0) { ScrollLines(-(Rows - 2)); e.Handled = true; return; }
                Send("\u001b[6~"); e.Handled = true; return;
            case Key.Space:
                if (ctrl) { Send("\0"); e.Handled = true; return; }
                break;
        }

        if (ctrl && e.Key >= Key.A && e.Key <= Key.Z)
        {
            var ch = (char)(e.Key - Key.A + 1);
            Send(ch.ToString());
            e.Handled = true;
            return;
        }

        if (alt && e.Key >= Key.A && e.Key <= Key.Z)
        {
            Send("\u001b" + (char)('a' + (e.Key - Key.A)));
            e.Handled = true;
            return;
        }
    }

    private string CursorKey(string suffix)
        => Buffer.ApplicationCursorKeys ? $"\u001bO{suffix}" : $"\u001b[{suffix}";

    private static string FunctionKey(Key key) => key switch
    {
        Key.F1 => "\u001bOP", Key.F2 => "\u001bOQ", Key.F3 => "\u001bOR", Key.F4 => "\u001bOS",
        Key.F5 => "\u001b[15~", Key.F6 => "\u001b[17~", Key.F7 => "\u001b[18~", Key.F8 => "\u001b[19~",
        Key.F9 => "\u001b[20~", Key.F10 => "\u001b[21~", Key.F11 => "\u001b[23~", Key.F12 => "\u001b[24~",
        _ => ""
    };

    private void Send(string text) => Input?.Invoke(Encoding.UTF8.GetBytes(text));
    private void SendEscape(string text) => Input?.Invoke(Encoding.UTF8.GetBytes(text));

    // ---------------------------------------------------------------- mouse

    private (long line, int col) CellFromPoint(Point p)
    {
        var col = (int)Math.Floor(p.X / _cellWidth);
        var row = (int)Math.Floor(p.Y / _cellHeight);
        col = Math.Clamp(col, 0, Math.Max(0, Columns - 1));
        row = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
        return (_topLine + row, col);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();

        if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 1)
        {
            if (System.Windows.Clipboard.ContainsText()) Paste(System.Windows.Clipboard.GetText());
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) { base.OnMouseDown(e); return; }

        var (line, col) = CellFromPoint(e.GetPosition(this));

        if (Buffer.MouseMode > 0)
        {
            SendMouse('M', col, (int)line, e.ChangedButton);
            _mouseDown = true;
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var token = TokenAt(line, col);
            if (token is not null) { LinkActivated?.Invoke(token); e.Handled = true; return; }
        }

        if (e.ClickCount == 2)
        {
            SelectWord(line, col);
        }
        else if (e.ClickCount == 3)
        {
            SelectionStart = (line, 0);
            SelectionEnd = (line, Math.Max(0, Buffer.Columns - 1));
        }
        else
        {
            SelectionStart = (line, col);
            SelectionEnd = (line, col);
            _dragAnchor = e.GetPosition(this);
            _dragging = true;
            CaptureMouse();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Buffer.MouseMode > 0 && _mouseDown)
        {
            var (line, col) = CellFromPoint(e.GetPosition(this));
            SendMouse('M', col, (int)line, MouseButton.Left, drag: true);
            return;
        }

        if (Buffer.MouseMode == 0 && HasSelection && !_dragging && e.LeftButton == MouseButtonState.Released)
        {
            var (l, c) = CellFromPoint(e.GetPosition(this));
            Cursor = TokenAt(l, c) is not null ? Cursors.Hand : _ibeam;
        }

        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        // Auto-scroll when dragging past the top or bottom edge.
        if (pos.Y < 4) ScrollLines(2);
        else if (pos.Y > ActualHeight - 4) ScrollLines(-2);

        var (line2, col2) = CellFromPoint(pos);
        SelectionEnd = (line2, col2);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragging && e.ChangedButton == MouseButton.Left)
        {
            _dragging = false;
            ReleaseMouseCapture();
            if (SelectionStart.Equals(SelectionEnd)) ClearSelection();
        }
        if (Buffer.MouseMode > 0 && _mouseDown)
        {
            var (line, col) = CellFromPoint(e.GetPosition(this));
            SendMouse('m', col, (int)line, e.ChangedButton);
            _mouseDown = false;
        }
        // One announcement per click gesture, however the selection was made (drag, double or
        // triple click, plain click). Drag moves stay silent, so the event does not chatter.
        if (e.ChangedButton == MouseButton.Left && Buffer.MouseMode == 0) RaiseSelectionChanged();
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var lines = e.Delta > 0 ? 3 : -3;
        if (Buffer.MouseMode > 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var (line, col) = CellFromPoint(e.GetPosition(this));
            SendMouse(e.Delta > 0 ? 'M' : 'm', col, (int)line, MouseButton.Middle, wheel: e.Delta > 0 ? 65 : 64);
            e.Handled = true;
            return;
        }
        ScrollLines(lines);
        e.Handled = true;
    }

    private void SendMouse(char final, int col, int row, MouseButton button, bool drag = false, int wheel = -1)
    {
        if (Buffer.SgrMouse)
        {
            var b = wheel >= 0 ? wheel : button switch
            {
                MouseButton.Left => 0,
                MouseButton.Middle => 1,
                MouseButton.Right => 2,
                _ => 0
            };
            if (drag) b += 32;
            Input?.Invoke(Encoding.ASCII.GetBytes($"\u001b[<{b};{col + 1};{row + 1}{(final == 'm' ? 'm' : 'M')}"));
            return;
        }

        var x = Math.Clamp(col + 1, 1, 223);
        var y = Math.Clamp(row + 1, 1, 223);
        var code = wheel >= 0 ? wheel : (final == 'm' ? 3 : 0);
        Input?.Invoke(new[] { (byte)0x1b, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + x), (byte)(32 + y) });
    }

    private void SelectWord(long line, int col)
    {
        var cell = Buffer.LineAtAbsolute(line);
        if (cell is null) return;
        var text = new StringBuilder();
        foreach (var c in cell.Cells)
            text.Append(c.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(c.Codepoint));
        var s = text.ToString();

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '~' or '@';
        var start = Math.Clamp(col, 0, s.Length - 1);
        var end = start;
        while (start > 0 && IsWordChar(s[start - 1])) start--;
        while (end < s.Length - 1 && IsWordChar(s[end + 1])) end++;
        SelectionStart = (line, start);
        SelectionEnd = (line, end);
    }

    private string? TokenAt(long line, int col)
    {
        var termLine = Buffer.LineAtAbsolute(line);
        if (termLine is null) return null;
        var text = new StringBuilder();
        foreach (var c in termLine.Cells)
            text.Append(c.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(c.Codepoint));
        var s = text.ToString().TrimEnd();
        if (col < 0 || col >= s.Length) return null;

        var start = col;
        while (start > 0 && !char.IsWhiteSpace(s[start - 1])) start--;
        var end = col;
        while (end < s.Length - 1 && !char.IsWhiteSpace(s[end + 1])) end++;
        var token = s[start..(end + 1)].Trim('(', ')', '[', ']', ',', ';', '"', '\'');

        if (token.StartsWith("http://") || token.StartsWith("https://")) return token;
        if (token.StartsWith("www.")) return "https://" + token;
        if (token.StartsWith("/") || token.StartsWith("~/")) return "file:" + token;
        return null;
    }

    // ---------------------------------------------------------------- helpers

    private struct CachedRow
    {
        public int Version;
        public int Theme;
        public int Font;
        public double Width;
        public DrawingGroup Group;
    }

    private sealed class DispatcherTimerBridge
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer;

        public DispatcherTimerBridge(TimeSpan interval, Action tick)
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public bool IsRunning => _timer.IsEnabled;
    }
}
