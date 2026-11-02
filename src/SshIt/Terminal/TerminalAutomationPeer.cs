using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Text;
using System.Windows.Automation.Provider;

namespace SshIt.Terminal;

/// <summary>
/// Exposes the terminal to accessibility tools. The screen is a custom-drawn
/// FrameworkElement, so without this a screen reader sees an empty box.
///
/// TextPattern covers the visible viewport, one line per row, and implements the whole
/// provider: character, word, line, paragraph and document movement, endpoint movement,
/// unit expansion, Find, selection (wired to the terminal's own selection, so a range
/// highlights where it is read) and bounding rectangles. ValuePattern carries the same
/// text for tools that prefer it.
///
/// Boundaries, so behaviour is predictable rather than guessed: a word is a run of
/// letters, digits and underscore, or a run of other non-space characters; whitespace
/// separates them. A line is a screen row, which is what a reader can address on screen
/// (a soft-wrapped logical line is several rows). A paragraph is a run of non-blank rows.
/// Movement never splits a surrogate pair or a base character from its combining marks.
/// </summary>
public sealed class TerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, IValueProvider, IRawElementProviderSimple
{
    private readonly TerminalView _view;
    private string _cachedText = "";
    private int[] _rowStarts = Array.Empty<int>();
    private int[] _rowEnds = Array.Empty<int>();
    private DateTime _cachedAt = DateTime.MinValue;

    public TerminalAutomationPeer(TerminalView owner) : base(owner)
    {
        _view = owner;
        _view.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        try { RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged); }
        catch { /* no listeners, or the peer is gone */ }
    }

    // ---------------------------------------------------------------- identity

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetClassNameCore() => "TerminalView";

    protected override string GetNameCore() => "Terminal";

    protected override string GetHelpTextCore()
        => "Terminal output. Read with a screen reader; keyboard input goes to the session.";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface is PatternInterface.Text or PatternInterface.Value
            ? this
            : base.GetPattern(patternInterface);

    // ---------------------------------------------------------------- text snapshot

    internal int VisibleRows => _view.Rows;

    internal long TopVisibleLine => _view.TopVisibleLine;

    /// <summary>Viewport text: one line per visible row, trailing blanks trimmed.</summary>
    public string Text
    {
        get
        {
            if ((DateTime.UtcNow - _cachedAt).TotalMilliseconds < 250) return _cachedText;

            var rows = VisibleRows;
            var starts = new int[rows];
            var ends = new int[rows];
            var builder = new StringBuilder();
            for (var row = 0; row < rows; row++)
            {
                starts[row] = builder.Length;
                builder.Append(VisibleRowText(row));
                ends[row] = builder.Length;
                if (row < rows - 1) builder.Append('\n');
            }

            _rowStarts = starts;
            _rowEnds = ends;
            _cachedText = builder.ToString();
            _cachedAt = DateTime.UtcNow;
            return _cachedText;
        }
    }

    private TermLine? VisibleLine(int row)
    {
        var global = _view.TopVisibleLine + row;
        if (global < 0 || global >= _view.Buffer.TotalLines) return null;
        return _view.Buffer.LineAtAbsolute(global);
    }

    private string VisibleRowText(int row) => CellsToText(VisibleLine(row));

    private static string CellsToText(TermLine? line)
    {
        if (line is null) return "";
        var builder = new StringBuilder();
        foreach (var cell in line.Cells)
        {
            if (cell.Width == 0) continue;
            builder.Append(cell.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>Tells assistive tech that output arrived. Throttled by the caller.</summary>
    public void NotifyTextChanged()
    {
        try { RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged); }
        catch { /* no listeners, or the peer is gone */ }
    }

    // ---------------------------------------------------------------- row and cell mapping

    /// <summary>Row that holds a character offset; the last row for an offset past the end.</summary>
    internal int RowOf(int offset)
    {
        var text = Text;
        if (_rowStarts.Length == 0) return 0;
        offset = Math.Clamp(offset, 0, text.Length);

        var low = 0;
        var high = _rowStarts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_rowStarts[mid] <= offset) low = mid; else high = mid - 1;
        }
        return low;
    }

    /// <summary>Cell column of the character at a character index inside one row's text.</summary>
    internal int CellColumnAt(int row, int charIndex)
    {
        var line = VisibleLine(row);
        if (line is null) return 0;

        var position = 0;
        for (var column = 0; column < line.Cells.Length; column++)
        {
            var cell = line.Cells[column];
            if (cell.Width == 0) continue;
            var length = CellTextLength(cell);
            if (charIndex < position + length) return column;
            position += length;
        }
        return Math.Max(0, line.Cells.Length - 1);
    }

    /// <summary>Character index of the first character at or after a cell column of a row.</summary>
    internal int CharIndexAt(int row, int column)
    {
        var line = VisibleLine(row);
        if (line is null) return 0;

        var position = 0;
        for (var index = 0; index < line.Cells.Length && index < column; index++)
        {
            var cell = line.Cells[index];
            if (cell.Width == 0) continue;
            position += CellTextLength(cell);
        }
        return Math.Min(position, VisibleRowText(row).Length);
    }

    private static int CellTextLength(in Cell cell)
        => cell.Codepoint <= 0 ? 1 : cell.Codepoint > 0xFFFF ? 2 : 1;

    /// <summary>Absolute buffer position of the character at a text offset.</summary>
    internal (long Line, int Column) BufferPosition(int offset)
    {
        var text = Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var row = RowOf(offset);
        var charIndex = offset - _rowStarts[row];
        return (TopVisibleLine + row, CellColumnAt(row, charIndex));
    }

    /// <summary>Text offset of an absolute buffer position, clamped to the viewport.</summary>
    internal int OffsetAt(long line, int column)
    {
        var text = Text;
        var row = line - TopVisibleLine;
        if (row < 0) return 0;
        if (row >= _rowStarts.Length) return text.Length;
        return Math.Min(_rowStarts[(int)row] + CharIndexAt((int)row, Math.Max(0, column)), text.Length);
    }

    // ---------------------------------------------------------------- ITextProvider

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider DocumentRange => new TerminalTextRange(this, 0, Text.Length);

    public ITextRangeProvider[] GetVisibleRanges() => new ITextRangeProvider[] { DocumentRange };

    public ITextRangeProvider[] GetSelection()
    {
        if (!_view.HasSelection) return Array.Empty<ITextRangeProvider>();

        var (startLine, startColumn) = _view.SelectionStart;
        var (endLine, endColumn) = _view.SelectionEnd;
        if (startLine > endLine || (startLine == endLine && startColumn > endColumn))
        {
            (startLine, startColumn, endLine, endColumn) = (endLine, endColumn, startLine, startColumn);
        }

        var start = OffsetAt(startLine, startColumn);
        var end = OffsetAt(endLine, endColumn + 1);
        if (end <= start) return Array.Empty<ITextRangeProvider>();
        return new ITextRangeProvider[] { new TerminalTextRange(this, start, end) };
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
        => throw new ElementNotAvailableException("The terminal has no child elements.");

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        try
        {
            var local = _view.PointFromScreen(screenLocation);
            var row = (int)Math.Floor(local.Y / Math.Max(1, _view.CellHeight));
            var column = (int)Math.Floor(local.X / Math.Max(1, _view.CellWidth));
            row = Math.Clamp(row, 0, Math.Max(0, _rowStarts.Length - 1));
            var offset = _rowStarts[row] + CharIndexAt(row, Math.Max(0, column));
            return new TerminalTextRange(this, offset, offset);
        }
        catch
        {
            return DocumentRange;
        }
    }

    // ---------------------------------------------------------------- selection and geometry bridge

    /// <summary>Applies a character range to the terminal's own selection, so it highlights.</summary>
    internal void SelectRange(int start, int end)
    {
        var text = Text;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (end == start)
        {
            _view.ClearSelection();
            return;
        }

        var (startLine, startColumn) = BufferPosition(start);
        var (endLine, endColumn) = BufferPosition(end - 1);
        _view.SetSelection(startLine, startColumn, endLine, endColumn);
    }

    internal void ClearSelection() => _view.ClearSelection();

    internal void ScrollTo(long line) => _view.ScrollToLine(line);

    /// <summary>Screen rectangles, four doubles each, one per row the range covers.</summary>
    internal double[] RectanglesFor(int start, int end)
    {
        var text = Text;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (_rowStarts.Length == 0) return Array.Empty<double>();

        Point origin;
        try { origin = _view.PointToScreen(new Point(0, 0)); }
        catch { return Array.Empty<double>(); }

        var last = end > start ? end - 1 : start;
        var firstRow = RowOf(start);
        var lastRow = RowOf(last);
        var rectangles = new List<double>();

        for (var row = firstRow; row <= lastRow; row++)
        {
            var rowStart = _rowStarts[row];
            var rowEnd = _rowEnds[row];
            var from = Math.Max(start, rowStart) - rowStart;
            var to = Math.Min(Math.Max(end, start + 1), rowEnd) - rowStart;
            if (to <= from) continue;

            var startColumn = CellColumnAt(row, from);
            var lastColumn = CellColumnAt(row, to - 1);
            var cells = VisibleLine(row)?.Cells;
            var width = cells is not null && lastColumn >= 0 && lastColumn < cells.Length
                ? Math.Max(1, (int)cells[lastColumn].Width)
                : 1;
            var columns = lastColumn + width - startColumn;
            if (columns <= 0) continue;

            rectangles.Add(origin.X + startColumn * _view.CellWidth);
            rectangles.Add(origin.Y + row * _view.CellHeight);
            rectangles.Add(columns * _view.CellWidth);
            rectangles.Add(_view.CellHeight);
        }

        return rectangles.ToArray();
    }

    // ---------------------------------------------------------------- IValueProvider

    public bool IsReadOnly => true;

    public string Value => Text;

    public void SetValue(string value) => throw new InvalidOperationException("The terminal is read-only.");

    // ---------------------------------------------------------------- IRawElementProviderSimple
    // The peer doubles as the provider returned to a text range's GetEnclosingElement.

    ProviderOptions IRawElementProviderSimple.ProviderOptions => ProviderOptions.ServerSideProvider;

    object? IRawElementProviderSimple.GetPatternProvider(int patternId) => null;

    object? IRawElementProviderSimple.GetPropertyValue(int propertyId) => null;

    IRawElementProviderSimple? IRawElementProviderSimple.HostRawElementProvider => null;
}

/// <summary>
/// Range over the peer's viewport text. Offsets are clamped against the current text on
/// every call, because output can arrive between two calls.
/// </summary>
internal sealed class TerminalTextRange : ITextRangeProvider
{
    private readonly TerminalAutomationPeer _peer;
    private int _start;
    private int _end;

    public TerminalTextRange(TerminalAutomationPeer peer, int start, int end)
    {
        _peer = peer;
        _start = Math.Max(0, start);
        _end = Math.Max(_start, end);
    }

    private string Text => _peer.Text;

    private void Clamp()
    {
        var length = Text.Length;
        _start = Math.Clamp(_start, 0, length);
        _end = Math.Clamp(_end, _start, length);
    }

    public ITextRangeProvider Clone() => new TerminalTextRange(_peer, _start, _end);

    public bool Compare(ITextRangeProvider range)
        => range is TerminalTextRange other && other._start == _start && other._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
                                TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TerminalTextRange other) throw new ArgumentException("range", nameof(targetRange));
        var mine = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        var theirs = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        return mine.CompareTo(theirs);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        Clamp();
        var text = Text;
        if (text.Length == 0)
        {
            _start = _end = 0;
            return;
        }

        var first = Math.Clamp(_start, 0, text.Length - 1);
        var last = Math.Clamp(Math.Max(_start, _end - 1), 0, text.Length - 1);

        switch (unit)
        {
            case TextUnit.Character:
                _start = TerminalTextUnits.ElementStart(text, first);
                _end = TerminalTextUnits.ElementEnd(text, last);
                break;
            case TextUnit.Word:
                _start = TerminalTextUnits.WordSpan(text, first).Start;
                _end = TerminalTextUnits.WordSpan(text, last).End;
                break;
            case TextUnit.Line:
                _start = TerminalTextUnits.LineStart(text, first);
                _end = TerminalTextUnits.LineEnd(text, last);
                break;
            case TextUnit.Paragraph:
                _start = TerminalTextUnits.ParagraphSpan(text, first).Start;
                _end = TerminalTextUnits.ParagraphSpan(text, last).End;
                break;
            case TextUnit.Document:
                _start = 0;
                _end = text.Length;
                break;
            default:
                // Format has no meaning over a terminal cell grid; leave the range alone.
                break;
        }
    }

    public ITextRangeProvider FindAttribute(int attribute, object value, bool backward) => null!;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null!;
        Clamp();

        var haystack = Text;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // The pattern searches the whole document from the range, not only inside it, so a
        // reader that moves a caret and then searches still finds the text.
        if (backward)
        {
            var limit = Math.Min(_start + text.Length, haystack.Length);
            var index = limit > 0 ? haystack.Substring(0, limit).LastIndexOf(text, comparison) : -1;
            if (index < 0) index = haystack.LastIndexOf(text, comparison);
            return index < 0 ? null! : new TerminalTextRange(_peer, index, index + text.Length);
        }

        var forward = haystack.IndexOf(text, Math.Min(_start, haystack.Length), comparison);
        if (forward < 0) forward = haystack.IndexOf(text, 0, comparison);
        return forward < 0 ? null! : new TerminalTextRange(_peer, forward, forward + text.Length);
    }

    public object GetAttributeValue(int attribute) => null!;

    public double[] GetBoundingRectangles() => _peer.RectanglesFor(_start, _end);

    public IRawElementProviderSimple GetEnclosingElement() => _peer;

    public string GetText(int maxLength)
    {
        Clamp();
        var slice = Text.Substring(_start, _end - _start);
        return maxLength >= 0 && slice.Length > maxLength ? slice.Substring(0, maxLength) : slice;
    }

    public int Move(TextUnit unit, int count)
    {
        Clamp();
        var text = Text;
        var position = count >= 0 ? _end : _start;

        if (count == 0)
        {
            _start = _end = _start;
            return 0;
        }

        var moved = 0;
        for (var step = 0; step < Math.Abs(count); step++)
        {
            var next = count > 0
                ? TerminalTextUnits.StepForward(text, unit, position)
                : TerminalTextUnits.StepBackward(text, unit, position);
            if (next == position) break;
            position = next;
            moved++;
        }

        _start = _end = position;
        return count > 0 ? moved : -moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        Clamp();
        var text = Text;
        var position = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        if (count == 0) return 0;

        var moved = 0;
        for (var step = 0; step < Math.Abs(count); step++)
        {
            // An end endpoint advances to the end of the next unit, not to its start, so a
            // line range grows to cover the whole next line instead of stopping at its edge.
            var next = count > 0
                ? (endpoint == TextPatternRangeEndpoint.End
                    ? TerminalTextUnits.StepForwardEnd(text, unit, position)
                    : TerminalTextUnits.StepForward(text, unit, position))
                : (endpoint == TextPatternRangeEndpoint.End
                    ? TerminalTextUnits.StepBackwardEnd(text, unit, position)
                    : TerminalTextUnits.StepBackward(text, unit, position));
            if (next == position) break;

            // Endpoints do not cross: a move that would pass the other end stops there.
            if (endpoint == TextPatternRangeEndpoint.Start)
            {
                if (next > _end) break;
                _start = next;
            }
            else
            {
                if (next < _start) break;
                _end = next;
            }

            position = next;
            moved++;
        }

        return count > 0 ? moved : -moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
                                    TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TerminalTextRange other) return;
        Clamp();
        var value = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        if (endpoint == TextPatternRangeEndpoint.Start) _start = Math.Min(value, _end);
        else _end = Math.Max(value, _start);
    }

    public void Select()
    {
        Clamp();
        _peer.SelectRange(_start, _end);
    }

    // The terminal has one selection, which is what the mouse makes, so adding to it is
    // selecting, and removing only has meaning when the range is the current selection.
    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
        Clamp();
        if (_peer.GetSelection() is { Length: 1 } selection
            && selection[0] is TerminalTextRange current
            && current._start == _start && current._end == _end)
        {
            _peer.ClearSelection();
        }
    }

    public void ScrollIntoView(bool alignToTop)
    {
        Clamp();
        var (line, _) = _peer.BufferPosition(_start);
        var row = line - _peer.TopVisibleLine;
        if (row < 0 || row >= _peer.VisibleRows) _peer.ScrollTo(line);
    }

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();
}

/// <summary>
/// Unit boundaries over the published text. Plain string arithmetic: the terminal's text is
/// a screen grid, so there is no markup or embedded object to step over.
/// </summary>
internal static class TerminalTextUnits
{
    /// <summary>0 for whitespace, 1 for letters/digits/underscore, 2 for anything else.</summary>
    public static int ClassAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return 0;

        var c = text[index];
        if (char.IsWhiteSpace(c)) return 0;
        if (char.IsLowSurrogate(c)) return 2;
        if (char.IsHighSurrogate(c))
        {
            if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                return Rune.IsLetterOrDigit(new Rune(c, text[index + 1])) ? 1 : 2;
            return 2;
        }
        return char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;
    }

    public static int RuneLengthAt(string text, int index)
        => index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1])
            ? 2
            : 1;

    private static int PrevRune(string text, int index)
    {
        var previous = index - 1;
        if (previous > 0 && char.IsLowSurrogate(text[previous]) && char.IsHighSurrogate(text[previous - 1]))
            previous--;
        return previous;
    }

    public static int NextCharacter(string text, int position)
    {
        if (position >= text.Length) return text.Length;

        var next = position + RuneLengthAt(text, position);
        while (next < text.Length)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(text, next);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                next += RuneLengthAt(text, next);
                continue;
            }
            break;
        }
        return next;
    }

    public static int PrevCharacter(string text, int position)
    {
        if (position <= 0) return 0;

        var index = PrevRune(text, position);
        while (index > 0)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                index = PrevRune(text, index);
                continue;
            }
            break;
        }
        return index;
    }

    public static int ElementStart(string text, int position)
    {
        if (position <= 0) return 0;
        if (position >= text.Length) return text.Length;

        var category = CharUnicodeInfo.GetUnicodeCategory(text, position);
        var continuation = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark || char.IsLowSurrogate(text[position]);
        return continuation ? PrevCharacter(text, position) : position;
    }

    public static int ElementEnd(string text, int position)
        => NextCharacter(text, ElementStart(text, position));

    public static int LineStart(string text, int position)
    {
        if (position <= 0) return 0;
        var index = text.LastIndexOf('\n', Math.Min(position, text.Length) - 1);
        return index < 0 ? 0 : index + 1;
    }

    public static int LineEnd(string text, int position)
    {
        if (position >= text.Length) return text.Length;
        var index = text.IndexOf('\n', Math.Max(0, position));
        return index < 0 ? text.Length : index;
    }

    public static (int Start, int End) WordSpan(string text, int position)
    {
        if (text.Length == 0) return (0, 0);
        if (position >= text.Length) return (text.Length, text.Length);

        position = ElementStart(text, Math.Max(0, position));
        var cls = ClassAt(text, position);

        var start = position;
        while (start > 0)
        {
            var previous = PrevRune(text, start);
            if (ClassAt(text, previous) != cls) break;
            start = previous;
        }

        var end = position;
        while (end < text.Length && ClassAt(text, end) == cls) end += RuneLengthAt(text, end);
        return (start, end);
    }

    public static (int Start, int End) ParagraphSpan(string text, int position)
    {
        if (text.Length == 0) return (0, 0);

        position = Math.Clamp(position, 0, text.Length - 1);
        var start = LineStart(text, position);
        var end = LineEnd(text, position);
        if (IsBlank(text, start, end)) return (start, end);

        while (start > 0)
        {
            var previousEnd = start - 1;
            var previousStart = LineStart(text, previousEnd);
            if (IsBlank(text, previousStart, previousEnd)) break;
            start = previousStart;
        }

        while (end < text.Length)
        {
            var nextStart = end + 1;
            var nextEnd = LineEnd(text, nextStart);
            if (IsBlank(text, nextStart, nextEnd)) break;
            end = nextEnd;
        }

        return (start, end);
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (!char.IsWhiteSpace(text[index])) return false;
        return true;
    }

    /// <summary>Span of the unit that holds a position; used to move range endpoints.</summary>
    public static (int Start, int End) SpanAt(string text, TextUnit unit, int position)
    {
        if (text.Length == 0) return (0, 0);
        position = Math.Clamp(position, 0, text.Length - 1);

        switch (unit)
        {
            case TextUnit.Character:
                return (ElementStart(text, position), ElementEnd(text, position));
            case TextUnit.Word:
                return WordSpan(text, position);
            case TextUnit.Line:
                return (LineStart(text, position), LineEnd(text, position));
            case TextUnit.Paragraph:
                return ParagraphSpan(text, position);
            default:
                return (0, text.Length);
        }
    }

    /// <summary>
    /// Forward step for an end endpoint: finish the unit it is in, then move to the end of
    /// the next one. A caret step lands on unit starts, an end endpoint on unit ends.
    /// </summary>
    public static int StepForwardEnd(string text, TextUnit unit, int position)
    {
        if (position >= text.Length) return text.Length;

        var (_, end) = SpanAt(text, unit, position);
        if (position < end) return end;
        if (end >= text.Length) return text.Length;
        return SpanAt(text, unit, end + 1).End;
    }

    /// <summary>Backward step for an end endpoint: the end of the previous unit.</summary>
    public static int StepBackwardEnd(string text, TextUnit unit, int position)
    {
        if (position <= 0 || text.Length == 0) return 0;

        var (start, _) = SpanAt(text, unit, Math.Min(position, text.Length - 1));
        if (start <= 0) return 0;
        return SpanAt(text, unit, start - 1).End;
    }

    public static int NextWord(string text, int position)
    {
        if (position >= text.Length) return text.Length;

        position = ElementStart(text, Math.Max(0, position));
        var cls = ClassAt(text, position);
        if (cls != 0)
        {
            var end = position;
            while (end < text.Length && ClassAt(text, end) == cls) end += RuneLengthAt(text, end);
            position = end;
        }

        while (position < text.Length && ClassAt(text, position) == 0) position++;
        return position;
    }

    public static int PrevWord(string text, int position)
    {
        if (position <= 0) return 0;
        position = Math.Min(position, text.Length);

        while (position > 0)
        {
            var previous = PrevRune(text, position);
            if (ClassAt(text, previous) != 0) break;
            position = previous;
        }
        if (position == 0) return 0;

        var cls = ClassAt(text, PrevRune(text, position));
        while (position > 0)
        {
            var previous = PrevRune(text, position);
            if (ClassAt(text, previous) != cls) break;
            position = previous;
        }
        return position;
    }

    public static int StepForward(string text, TextUnit unit, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        switch (unit)
        {
            case TextUnit.Character:
                return NextCharacter(text, position);
            case TextUnit.Word:
                return NextWord(text, position);
            case TextUnit.Line:
            {
                var end = LineEnd(text, position);
                return end >= text.Length ? text.Length : end + 1;
            }
            case TextUnit.Paragraph:
            {
                var (_, end) = ParagraphSpan(text, Math.Min(position, Math.Max(0, text.Length - 1)));
                if (end >= text.Length) return text.Length;

                var next = end + 1;
                while (next <= text.Length)
                {
                    var nextEnd = LineEnd(text, next);
                    if (!IsBlank(text, next, nextEnd)) return next;
                    if (nextEnd >= text.Length) return text.Length;
                    next = nextEnd + 1;
                }
                return text.Length;
            }
            default:
                return text.Length;
        }
    }

    public static int StepBackward(string text, TextUnit unit, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        switch (unit)
        {
            case TextUnit.Character:
                return PrevCharacter(text, position);
            case TextUnit.Word:
                return PrevWord(text, position);
            case TextUnit.Line:
            {
                var start = LineStart(text, position);
                if (start < position) return start;
                if (start == 0) return 0;
                return LineStart(text, start - 1);
            }
            case TextUnit.Paragraph:
            {
                if (text.Length == 0) return 0;

                var probe = Math.Min(position, text.Length - 1);
                var blockStart = ParagraphSpan(text, probe).Start;
                if (position > blockStart) return blockStart;
                if (blockStart == 0) return 0;

                var line = LineStart(text, blockStart - 1);
                while (true)
                {
                    if (!IsBlank(text, line, LineEnd(text, line))) return ParagraphSpan(text, line).Start;
                    if (line == 0) return 0;
                    line = LineStart(text, line - 1);
                }
            }
            default:
                return 0;
        }
    }
}
