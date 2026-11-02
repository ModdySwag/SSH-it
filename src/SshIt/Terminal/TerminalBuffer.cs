namespace SshIt.Terminal;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Reverse = 32,
    Hidden = 64,
    Strike = 128
}

/// <summary>Colour is packed: 0 = default, 0x01xxxxxx = palette index, 0xFFxxxxxx = RGB.</summary>
public static class TermColor
{
    public const uint Default = 0;

    public static uint Indexed(int index) => 0x01000000u | (uint)(index & 0xFF);
    public static uint Rgb(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    public static bool IsDefault(uint c) => (c >> 24) == 0x00;
    public static bool IsIndexed(uint c) => (c >> 24) == 0x01;
    public static bool IsRgb(uint c) => (c >> 24) == 0xFF;

    public static int Index(uint c) => (int)(c & 0xFF);
    public static (byte r, byte g, byte b) RgbParts(uint c) => ((byte)(c >> 16), (byte)(c >> 8), (byte)c);
}

public struct Cell
{
    public int Codepoint;      // 0 renders as blank
    public uint Fg;
    public uint Bg;
    public CellFlags Flags;
    public byte Width;         // 1 normal, 2 wide, 0 trailing half of a wide glyph

    public static Cell Blank(uint bg = 0) => new() { Codepoint = ' ', Width = 1, Bg = bg };

    public readonly bool IsContinuation => Width == 0;

    public readonly Cell WithFg(uint fg) { var c = this; c.Fg = fg; return c; }
    public readonly Cell WithBg(uint bg) { var c = this; c.Bg = bg; return c; }

    public readonly bool SameVisual(in Cell other)
        => Codepoint == other.Codepoint && Fg == other.Fg && Bg == other.Bg && Flags == other.Flags;
}

public sealed class TermLine
{
    public Cell[] Cells;
    public bool Wrapped;          // soft-wrapped into the next line
    public bool Dirty = true;
    public int Version;

    public TermLine(int columns)
    {
        Cells = new Cell[Math.Max(1, columns)];
        for (var i = 0; i < Cells.Length; i++) Cells[i] = Cell.Blank();
    }

    public TermLine Clone()
    {
        return new TermLine(1)
        {
            Cells = (Cell[])Cells.Clone(),
            Wrapped = Wrapped,
            Dirty = true
        };
    }

    public void Resize(int columns)
    {
        if (columns == Cells.Length) return;
        var next = new Cell[Math.Max(1, columns)];
        var copy = Math.Min(columns, Cells.Length);
        Array.Copy(Cells, next, copy);
        for (var i = copy; i < next.Length; i++) next[i] = Cell.Blank();
        Cells = next;
        Dirty = true;
    }
}

/// <summary>
/// Screen model for one terminal session: primary buffer with scrollback, alternate
/// buffer, cursor, scroll region, pen attributes and terminal modes.
/// </summary>
public sealed class TerminalBuffer
{
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; set; } = 10000;

    public TermLine[] Screen { get; private set; } = Array.Empty<TermLine>();
    public List<TermLine> Scrollback { get; } = new();

    private TermLine[] _altScreen = Array.Empty<TermLine>();
    private bool _altActive;

    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public int CursorShape { get; private set; }      // 0 block, 1 bar, 2 underline
    public bool CursorBlink { get; private set; } = true;

    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    public bool AutoWrap { get; private set; } = true;
    public bool OriginMode { get; private set; }
    public bool InsertMode { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool NewLineMode { get; private set; }
    public bool ReverseVideo { get; private set; }

    /// <summary>0 none, 1 x10, 2 normal, 3 button, 4 any; SgrMouse adds 64.</summary>
    public int MouseMode { get; private set; }
    public bool SgrMouse { get; private set; }

    /// <summary>OSC 133 shell-integration marks, in absolute line index.</summary>
    public List<PromptMark> Prompts { get; } = new();

    private long _promptSeq;

    public string Title { get; private set; } = "";
    public string? WorkingDirectory { get; private set; }

    private Cell _pen = Cell.Blank();
    private Cell _savedCursorPen;
    private int _savedRow, _savedCol;
    private readonly bool[] _tabs = new bool[512];
    private int _lastPrinted;

    private char _g0 = 'B', _g1 = 'B', _g2 = 'B';
    private bool _glIsG1;
    private bool _wrapPending;
    private PromptMark? _openMark;
    private string? _pendingCommand;
    private long _commandStartLine = -1;

    public int Version { get; private set; }
    public bool ViewportFollowsOutput { get; set; } = true;

    public event Action? Damaged;
    public event Action<string>? TitleChanged;
    public event Action? BellRang;
    public event Action? PromptMarked;

    public TerminalBuffer(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        Screen = NewScreen(Rows);
        _altScreen = NewScreen(Rows);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        ResetTabs();
    }

    private TermLine[] NewScreen(int rows)
    {
        var lines = new TermLine[rows];
        for (var i = 0; i < rows; i++) lines[i] = new TermLine(Columns);
        return lines;
    }

    private void ResetTabs()
    {
        Array.Clear(_tabs);
        for (var i = 8; i < _tabs.Length; i += 8) _tabs[i] = true;
    }

    public TermLine ActiveLine(int row) => _altActive ? _altScreen[row] : Screen[row];

    public TermLine LineAt(int row) => ActiveLine(Math.Clamp(row, 0, Rows - 1));

    /// <summary>Global line index: 0 is the oldest scrollback line, scrollback+row is the screen.</summary>
    public TermLine? LineAtAbsolute(long index)
    {
        if (index < 0) return null;
        if (index < Scrollback.Count) return Scrollback[(int)index];
        var row = (int)(index - Scrollback.Count);
        return row >= 0 && row < Rows ? ActiveLine(row) : null;
    }

    public long TotalLines => (_altActive ? 0 : Scrollback.Count) + Rows;

    private void Touch(int row)
    {
        if (row < 0 || row >= Rows) return;
        ActiveLine(row).Dirty = true;
        ActiveLine(row).Version++;
        Version++;
    }

    private void DirtyAll()
    {
        foreach (var line in Screen) { line.Dirty = true; line.Version++; }
        Version++;
        Damaged?.Invoke();
    }

    public void MarkDamaged() => Damaged?.Invoke();

    // ---------------------------------------------------------------- output

    public void Write(int codepoint)
    {
        codepoint = MapGraphics(codepoint);
        var width = CharWidth(codepoint);

        if (width == 0)
        {
            // Combining mark: attach to the previous cell.
            var (r, c) = PreviousCell();
            if (r >= 0)
            {
                var prevLine = ActiveLine(r);
                if (c >= 0 && c < Columns)
                {
                    // Approximation: keep the base glyph, never lose the cell.
                    Touch(r);
                }
            }
            return;
        }

        // A character arriving after the last column was filled starts a new line.
        if (_wrapPending)
        {
            _wrapPending = false;
            ActiveLine(CursorRow).Wrapped = true;
            Touch(CursorRow);
            CursorCol = 0;
            LineFeedInternal();
        }

        if (AutoWrap && CursorCol + width > Columns)
        {
            ActiveLine(CursorRow).Wrapped = true;
            Touch(CursorRow);
            CursorCol = 0;
            LineFeedInternal();
        }
        else if (!AutoWrap && CursorCol + width > Columns)
        {
            CursorCol = Math.Max(0, Columns - width);
        }

        if (InsertMode) ShiftCellsRight(CursorCol, width);

        var row = CursorRow;
        var col = Math.Min(CursorCol, Math.Max(0, Columns - width));
        var line = ActiveLine(row);
        var cell = _pen;
        cell.Codepoint = codepoint;
        cell.Width = (byte)width;
        line.Cells[col] = cell;

        if (width == 2)
        {
            if (col + 1 < Columns)
                line.Cells[col + 1] = new Cell { Codepoint = 0, Width = 0, Bg = _pen.Bg };
            else
                line.Cells[col].Width = 1;
            _lastPrinted = col + 1;
        }
        else
        {
            _lastPrinted = col;
        }

        CursorCol += width;
        if (CursorCol >= Columns)
        {
            CursorCol = Columns - 1;
            _wrapPending = AutoWrap;
        }
        Touch(row);
    }

    private (int row, int col) PreviousCell()
    {
        var col = CursorCol - 1;
        var row = CursorRow;
        if (col < 0) return (-1, -1);
        return (row, col);
    }

    public void LineFeed()
    {
        LineFeedInternal();
        if (NewLineMode) CursorCol = 0;
    }

    private void LineFeedInternal()
    {
        if (CursorRow == ScrollBottom)
            ScrollUp(1);
        else if (CursorRow < Rows - 1)
            CursorRow++;
    }

    public void ReverseIndex()
    {
        if (CursorRow == ScrollTop)
            ScrollDown(1);
        else if (CursorRow > 0)
            CursorRow--;
    }

    public void CarriageReturn() { CursorCol = 0; _wrapPending = false; }
    public void Backspace() { CursorCol = Math.Max(0, CursorCol - 1); _wrapPending = false; }

    public void Tab(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var col = CursorCol + 1;
            while (col < Columns - 1 && !IsTabStop(col)) col++;
            CursorCol = Math.Min(col, Columns - 1);
        }
    }

    public void BackTab(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var col = CursorCol - 1;
            while (col > 0 && !IsTabStop(col)) col--;
            CursorCol = Math.Max(0, col);
        }
    }

    private bool IsTabStop(int col) => col > 0 && col < _tabs.Length && _tabs[col];

    public void SetTabStop() { if (CursorCol < _tabs.Length) _tabs[CursorCol] = true; }
    public void ClearTabStop(int mode)
    {
        if (mode == 0) { if (CursorCol < _tabs.Length) _tabs[CursorCol] = false; }
        else Array.Clear(_tabs);
    }

    // ---------------------------------------------------------------- cursor

    public void SetCursor(int row, int col)
    {
        var minRow = OriginMode ? ScrollTop : 0;
        var maxRow = OriginMode ? ScrollBottom : Rows - 1;
        CursorRow = Math.Clamp(row, minRow, maxRow);
        CursorCol = Math.Clamp(col, 0, Columns - 1);
        _wrapPending = false;
    }

    public void MoveCursor(int dRow, int dCol)
        => SetCursor(CursorRow + dRow, CursorCol + dCol);

    public void SetCursorVisible(bool visible) => CursorVisible = visible;
    public void SetCursorShape(int shape, bool blink) { CursorShape = shape; CursorBlink = blink; }

    public void SaveCursor()
    {
        _savedRow = CursorRow;
        _savedCol = CursorCol;
        _savedCursorPen = _pen;
    }

    public void RestoreCursor()
    {
        CursorRow = Math.Clamp(_savedRow, 0, Rows - 1);
        CursorCol = Math.Clamp(_savedCol, 0, Columns - 1);
        _pen = _savedCursorPen;
        Touch(CursorRow);
    }

    public void SetScrollRegion(int top, int bottom)
    {
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top >= bottom) return;
        ScrollTop = top;
        ScrollBottom = bottom;
        SetCursor(OriginMode ? 0 : 0, 0);
    }

    // ---------------------------------------------------------------- erase

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var r = CursorRow + 1; r < Rows; r++) ClearLine(r);
                break;
            case 1:
                EraseInLine(1);
                for (var r = 0; r < CursorRow; r++) ClearLine(r);
                break;
            case 2:
                for (var r = 0; r < Rows; r++) ClearLine(r);
                break;
            case 3:
                Scrollback.Clear();
                for (var r = 0; r < Rows; r++) ClearLine(r);
                break;
        }
        Touch(CursorRow);
    }

    public void EraseInLine(int mode)
    {
        var line = ActiveLine(CursorRow);
        var from = mode switch { 0 => CursorCol, 1 => 0, _ => 0 };
        var to = mode switch { 0 => Columns - 1, 1 => CursorCol, _ => Columns - 1 };
        for (var c = from; c <= to && c < Columns; c++)
            line.Cells[c] = Cell.Blank(_pen.Bg);
        Touch(CursorRow);
    }

    private void ClearLine(int row)
    {
        var line = ActiveLine(row);
        for (var c = 0; c < Columns; c++) line.Cells[c] = Cell.Blank(_pen.Bg);
        line.Wrapped = false;
        Touch(row);
    }

    public void EraseChars(int count)
    {
        var line = ActiveLine(CursorRow);
        for (var i = 0; i < count && CursorCol + i < Columns; i++)
            line.Cells[CursorCol + i] = Cell.Blank(_pen.Bg);
        Touch(CursorRow);
    }

    // ---------------------------------------------------------------- insert / delete

    public void InsertChars(int count)
    {
        ShiftCellsRight(CursorCol, count);
        Touch(CursorRow);
    }

    private void ShiftCellsRight(int at, int count)
    {
        var line = ActiveLine(CursorRow);
        if (at < 0 || at >= Columns) return;
        var span = Columns - at;
        for (var i = span - 1; i >= 0; i--)
        {
            var src = at + i;
            var dst = at + i + count;
            line.Cells[dst < Columns ? dst : Columns - 1] =
                src + count < Columns ? line.Cells[src] : Cell.Blank(_pen.Bg);
        }
        for (var i = 0; i < count && at + i < Columns; i++)
            line.Cells[at + i] = Cell.Blank(_pen.Bg);
        Touch(CursorRow);
    }

    public void DeleteChars(int count)
    {
        var line = ActiveLine(CursorRow);
        for (var i = CursorCol; i < Columns; i++)
        {
            var src = i + count;
            line.Cells[i] = src < Columns ? line.Cells[src] : Cell.Blank(_pen.Bg);
        }
        Touch(CursorRow);
    }

    public void InsertLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom) return;
        for (var n = 0; n < count; n++)
        {
            for (var r = ScrollBottom; r > CursorRow; r--)
                ActiveLine(r).Cells = (Cell[])ActiveLine(r - 1).Cells.Clone();
            ClearLine(CursorRow);
        }
        for (var r = CursorRow; r <= ScrollBottom; r++) Touch(r);
    }

    public void DeleteLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom) return;
        for (var n = 0; n < count; n++)
        {
            for (var r = CursorRow; r < ScrollBottom; r++)
                ActiveLine(r).Cells = (Cell[])ActiveLine(r + 1).Cells.Clone();
            ClearLine(ScrollBottom);
        }
        for (var r = CursorRow; r <= ScrollBottom; r++) Touch(r);
    }

    // ---------------------------------------------------------------- scrolling

    public int DebugScrollUps;

    public void ScrollUp(int count)
    {
        DebugScrollUps++;
        for (var n = 0; n < count; n++)
        {
            // Copy out before shifting, otherwise the scrollback entry and the
            // screen row would share the same Cell array.
            if (!_altActive && ScrollTop == 0)
            {
                var top = Screen[0];
                Scrollback.Add(new TermLine(Columns)
                {
                    Cells = (Cell[])top.Cells.Clone(),
                    Wrapped = top.Wrapped,
                    Dirty = false
                });
                TrimScrollback();
            }

            for (var r = ScrollTop; r < ScrollBottom; r++)
            {
                var dst = ScreenOrAlt(r);
                var src = ScreenOrAlt(r + 1);
                dst.Cells = (Cell[])src.Cells.Clone();
                dst.Wrapped = src.Wrapped;
            }

            var last = ActiveLine(ScrollBottom);
            for (var c = 0; c < Columns; c++) last.Cells[c] = Cell.Blank(_pen.Bg);
            last.Wrapped = false;
        }
        for (var r = ScrollTop; r <= ScrollBottom; r++) Touch(r);
        if (!_altActive) Damaged?.Invoke();
    }

    public void ScrollDown(int count)
    {
        for (var n = 0; n < count; n++)
        {
            for (var r = ScrollBottom; r > ScrollTop; r--)
                ScreenOrAlt(r).Cells = (Cell[])ScreenOrAlt(r - 1).Cells.Clone();
            var first = ActiveLine(ScrollTop);
            for (var c = 0; c < Columns; c++) first.Cells[c] = Cell.Blank(_pen.Bg);
            first.Wrapped = false;
        }
        for (var r = ScrollTop; r <= ScrollBottom; r++) Touch(r);
        Damaged?.Invoke();
    }

    private TermLine ScreenOrAlt(int row) => _altActive ? _altScreen[row] : Screen[row];

    private void TrimScrollback()
    {
        var limit = Math.Max(100, ScrollbackLimit);
        if (Scrollback.Count <= limit) return;
        var excess = Scrollback.Count - limit;
        Scrollback.RemoveRange(0, excess);

        // Keep shell-integration marks pointing at the right lines.
        if (Prompts.Count > 0)
        {
            for (var i = Prompts.Count - 1; i >= 0; i--)
            {
                Prompts[i].Line -= excess;
                if (Prompts[i].OutputLine >= 0) Prompts[i].OutputLine -= excess;
                if (Prompts[i].EndLine >= 0) Prompts[i].EndLine -= excess;
                if (Prompts[i].NextLine >= 0) Prompts[i].NextLine -= excess;
                if (Prompts[i].Line < 0) Prompts.RemoveAt(i);
            }
        }
        if (_commandStartLine >= 0) _commandStartLine -= excess;
    }

    /// <summary>
    /// Moves another buffer's history into this one. A tmux pane rebuilt for a new layout would
    /// otherwise lose everything above the capture; the old buffer is dead, so its lines are MOVED
    /// (not cloned) and resized to this buffer's width. Wide lines truncate at the new width — a
    /// split can be narrower, and reflowing arbitrary output would rewrap every line differently
    /// from what the server drew.
    /// </summary>
    public void AdoptHistoryFrom(TerminalBuffer other)
    {
        if (ReferenceEquals(this, other)) return;

        foreach (var line in other.Scrollback)
        {
            line.Resize(Columns);
            Scrollback.Add(line);
        }

        // The visible screen becomes history too. Trailing blank rows are dropped so the seam
        // between old history and the freshly captured screen is tight.
        var last = other.Rows - 1;
        while (last >= 0 && IsBlank(other.Screen[last])) last--;
        for (var row = 0; row <= last; row++)
        {
            var line = other.Screen[row];
            line.Resize(Columns);
            Scrollback.Add(line);
        }

        TrimScrollback();
        MarkDamaged();
    }

    private static bool IsBlank(TermLine line)
    {
        foreach (var cell in line.Cells)
            if (cell.Codepoint != 0 && cell.Codepoint != ' ') return false;
        return true;
    }

    // ---------------------------------------------------------------- alt screen

    public void EnterAltScreen(bool clear)
    {
        if (_altActive) return;
        _altActive = true;
        if (clear)
        {
            for (var r = 0; r < Rows; r++)
            {
                var line = _altScreen[r];
                for (var c = 0; c < Columns; c++) line.Cells[c] = Cell.Blank();
                line.Wrapped = false;
            }
            CursorRow = 0;
            CursorCol = 0;
            _wrapPending = false;
        }
        DirtyAll();
    }

    public void ExitAltScreen()
    {
        if (!_altActive) return;
        _altActive = false;
        DirtyAll();
    }

    public bool AltActive => _altActive;

    /// <summary>Raised on the UI thread when OSC 7 moves the shell into a new directory.</summary>
    public event Action<string>? WorkingDirectoryChanged;

    // ---------------------------------------------------------------- attributes

    public void SetPen(uint? fg = null, uint? bg = null, CellFlags? flags = null)
    {
        if (fg.HasValue) _pen.Fg = fg.Value;
        if (bg.HasValue) _pen.Bg = bg.Value;
        if (flags.HasValue) _pen.Flags = flags.Value;
    }

    public void ResetPen()
    {
        _pen = Cell.Blank();
    }

    public void Sgr(IReadOnlyList<int> p)
    {
        var i = 0;
        while (i < p.Count)
        {
            var code = p[i];
            switch (code)
            {
                case 0: _pen.Fg = 0; _pen.Bg = 0; _pen.Flags = CellFlags.None; break;
                case 1: _pen.Flags |= CellFlags.Bold; break;
                case 2: _pen.Flags |= CellFlags.Dim; break;
                case 3: _pen.Flags |= CellFlags.Italic; break;
                case 4: _pen.Flags |= CellFlags.Underline; break;
                case 5: case 6: _pen.Flags |= CellFlags.Blink; break;
                case 7: _pen.Flags |= CellFlags.Reverse; break;
                case 8: _pen.Flags |= CellFlags.Hidden; break;
                case 9: _pen.Flags |= CellFlags.Strike; break;
                case 21: case 22: _pen.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _pen.Flags &= ~CellFlags.Italic; break;
                case 24: _pen.Flags &= ~CellFlags.Underline; break;
                case 25: _pen.Flags &= ~CellFlags.Blink; break;
                case 27: _pen.Flags &= ~CellFlags.Reverse; break;
                case 28: _pen.Flags &= ~CellFlags.Hidden; break;
                case 29: _pen.Flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: _pen.Fg = TermColor.Indexed(code - 30); break;
                case 38: i = ReadExtendedColor(p, i, true); break;
                case 39: _pen.Fg = TermColor.Default; break;
                case >= 40 and <= 47: _pen.Bg = TermColor.Indexed(code - 40); break;
                case 48: i = ReadExtendedColor(p, i, false); break;
                case 49: _pen.Bg = TermColor.Default; break;
                case 58: i = ReadExtendedColor(p, i, false, underline: true); break;
                case 59: break;
                case >= 90 and <= 97: _pen.Fg = TermColor.Indexed(code - 90 + 8); break;
                case >= 100 and <= 107: _pen.Bg = TermColor.Indexed(code - 100 + 8); break;
            }
            i++;
        }
    }

    private int ReadExtendedColor(IReadOnlyList<int> p, int at, bool foreground, bool underline = false)
    {
        if (at + 1 >= p.Count) return at;
        var mode = p[at + 1];
        try
        {
            if (mode == 5 && at + 2 < p.Count)
            {
                var idx = TermColor.Indexed(p[at + 2]);
                if (foreground) _pen.Fg = idx; else if (!underline) _pen.Bg = idx;
                return at + 2;
            }
            if (mode == 2 && at + 4 < p.Count)
            {
                var rgb = TermColor.Rgb((byte)Math.Clamp(p[at + 2], 0, 255),
                                        (byte)Math.Clamp(p[at + 3], 0, 255),
                                        (byte)Math.Clamp(p[at + 4], 0, 255));
                if (foreground) _pen.Fg = rgb; else if (!underline) _pen.Bg = rgb;
                return at + 4;
            }
        }
        catch { }
        return at + 1;
    }

    public void SetMode(int mode, bool on)
    {
        switch (mode)
        {
            case 4: InsertMode = on; break;
            case 20: NewLineMode = on; break;
            case 1: ApplicationCursorKeys = on; break;
            case 6: OriginMode = on; SetCursor(0, 0); break;
            case 7: AutoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 66: ApplicationKeypad = on; break;
            case 47: case 1047: if (on) EnterAltScreen(true); else ExitAltScreen(); break;
            case 1049:
                if (on) { SaveCursor(); EnterAltScreen(true); }
                else { ExitAltScreen(); RestoreCursor(); }
                break;
            case 1000: MouseMode = on ? 2 : 0; break;
            case 1002: MouseMode = on ? 3 : 0; break;
            case 1003: MouseMode = on ? 4 : 0; break;
            case 1006: SgrMouse = on; break;
            case 2004: BracketedPaste = on; break;
            case 5: ReverseVideo = on; DirtyAll(); break;
            case 12: CursorBlink = on; break;
        }
    }

    public void SetCharacterSet(int index, char designator)
    {
        switch (index)
        {
            case 0: _g0 = designator; break;
            case 1: _g1 = designator; break;
            default: _g2 = designator; break;
        }
    }

    public void SelectCharacterSet(bool g1) => _glIsG1 = g1;

    /// <summary>DEC special graphics: the line-drawing set ncurses falls back to.</summary>
    private static readonly string Graphics =
        "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private int MapGraphics(int cp)
    {
        var set = _glIsG1 ? _g1 : _g0;
        if (set != '0') return cp;
        if (cp >= 0x60 && cp <= 0x7E)
        {
            var index = cp - 0x60;
            if (index < Graphics.Length) return Graphics[index];
        }
        return cp;
    }

    public void SetTitle(string title)
    {
        Title = title;
        TitleChanged?.Invoke(title);
    }

    public void SetWorkingDirectory(string path)
    {
        if (string.Equals(WorkingDirectory, path, StringComparison.Ordinal)) return;
        WorkingDirectory = path;
        WorkingDirectoryChanged?.Invoke(path);
    }

    public void Bell() => BellRang?.Invoke();

    /// <summary>Writes a dim informational line into the buffer (used for connection notices).</summary>
    public void AppendNotice(string text)
    {
        var fg = _pen.Fg;
        var bg = _pen.Bg;
        var flags = _pen.Flags;

        _pen.Fg = TermColor.Indexed(8);
        _pen.Bg = TermColor.Default;
        _pen.Flags = CellFlags.Dim;

        if (CursorCol > 0) { CarriageReturn(); LineFeed(); }
        foreach (var ch in text)
        {
            if (ch == '\n') LineFeed();
            else if (ch == '\r') CarriageReturn();
            else Write(ch);
        }
        CarriageReturn();
        LineFeed();

        _pen.Fg = fg;
        _pen.Bg = bg;
        _pen.Flags = flags;
        Touch(CursorRow);
        Damaged?.Invoke();
    }

    // ---------------------------------------------------------------- shell integration (OSC 133)

    public long AbsoluteCursorLine => (_altActive ? 0 : Scrollback.Count) + CursorRow;

    public void MarkPromptStart()
    {
        // A fresh prompt begins; close any command that never reported an exit code.
        if (_openMark is not null)
        {
            _openMark.EndLine = AbsoluteCursorLine;
            _openMark.Duration = DateTime.Now - _openMark.Started;
            Prompts.Add(_openMark);
            _openMark = null;
            PromptMarked?.Invoke();
        }

        // The new prompt is painted on this row, so the previous block ended above it. Only
        // when the cursor is at column 0: a command whose output had no trailing newline
        // leaves its last row shared with the prompt, and that row belongs to the output.
        if (CursorCol == 0 && Prompts.Count > 0)
        {
            var last = Prompts[^1];
            if (last.NextLine < 0) last.NextLine = AbsoluteCursorLine;
        }
    }


    public void MarkCommandStart()
    {
        // PS0 emits 133;C after the line is read, and the shell has already echoed the newline:
        // the cursor is on the OUTPUT row. The command sits on the row above, which is what the
        // scrape needs; a cursor still mid-line means the command row itself.
        _commandStartLine = CursorCol == 0 && AbsoluteCursorLine > 0
            ? AbsoluteCursorLine - 1
            : AbsoluteCursorLine;
    }

    /// <summary>OSC 633;E carries the exact command line, so no prompt text has to be scraped.</summary>
    public void SetCommandLine(string command) => _pendingCommand = command;

    public void MarkOutputStart()
    {
        // A shell that sent C without B still typed its command on the row above the cursor:
        // Enter has already moved down. That row is what the scrape reads.
        if (_commandStartLine < 0) _commandStartLine = Math.Max(0L, AbsoluteCursorLine - 1);


        _openMark = new PromptMark
        {
            Line = _commandStartLine >= 0 ? _commandStartLine : AbsoluteCursorLine,
            OutputLine = AbsoluteCursorLine,
            Cwd = WorkingDirectory,
            Command = _pendingCommand ?? ScrapeCommandLine(),
            Started = DateTime.Now
        };
        _pendingCommand = null;
    }

    public void MarkCommandFinished(int exitCode)
    {
        if (_openMark is null)
        {
            _openMark = new PromptMark
            {
                Line = AbsoluteCursorLine,
                OutputLine = AbsoluteCursorLine,
                Started = DateTime.Now
            };
        }
        _openMark.Command ??= ScrapeCommandLine();
        _openMark.ExitCode = exitCode;
        _openMark.EndLine = AbsoluteCursorLine;
        _openMark.Duration = DateTime.Now - _openMark.Started;
        var finished = _openMark;
        finished.Seq = ++_promptSeq;
        Prompts.Add(finished);
        _openMark = null;
        _commandStartLine = -1;
        if (Prompts.Count > 2000) Prompts.RemoveRange(0, Prompts.Count - 2000);
        PromptMarked?.Invoke();
        BlockFinished?.Invoke(finished);
    }

    /// <summary>
    /// A block completed with an exit status. Separate from PromptMarked because this is the
    /// evidence hook: it fires once per command, with the final mark, and never for a mark that
    /// was closed without an exit code.
    /// </summary>
    /// <summary>The text a finished block produced. The OSC 133 marks are not reliable row
    /// anchors — C can arrive before or after the first output row, D before the final newline,
    /// and a reflow rewrites every mark — so the range is trimmed by content: skip the row that
    /// carries the command, take the non-empty rows after it, and stop at the prompt.
    /// </summary>
    public string BlockOutput(PromptMark mark)
    {
        // Fish's D can arrive with the cursor above the output (its prompt redraw moves it),
        // leaving EndLine < OutputLine; never let the end fall above the first output row.
        var last = (int)Math.Min(Math.Max(mark.EndLine, mark.OutputLine), TotalLines);
        // The next prompt's own row is not this block's text, whenever the block is read after
        // that prompt was painted (a receipt, a copy, the diff view).
        if (mark.NextLine >= 0) last = (int)Math.Min(last, mark.NextLine - 1);
        if (last < 0) return "";

        string RowText(int row) => GetText(row, 0, row, Columns - 1, includeScrollback: true);

        // Find the echoed command row by scanning UP from the end: with a late C mark the mark's
        // Line points inside the output, so a forward search from it misses the first rows.
        var command = (mark.Command ?? "").Trim();
        var commandRow = -1;
        if (command.Length > 0)
        {
            var needle = command.Length > 24 ? command[..24] : command;
            for (var r = last; r >= 0; r--)
                if (RowText(r).Contains(needle, StringComparison.Ordinal)) { commandRow = r; break; }
        }

        var first = commandRow >= 0 ? commandRow + 1 : Math.Max(0, (int)mark.Line);
        if (first > last) return "";

        while (first <= last && RowText(first).Trim().Length == 0) first++;
        var end = last;
        while (end >= first && RowText(end).Trim().Length == 0) end--;
        if (end < first) return "";
        return GetText(first, 0, end, Columns - 1, includeScrollback: true);
    }


    public event Action<PromptMark>? BlockFinished;

    /// <summary>
    /// The typed command, when the shell did not send OSC 633;E. The prompt text lives on the
    /// same line, so the prefix up to the last prompt terminator is dropped - a heuristic, and
    /// the only reason the shell-integration snippet sends the exact line instead.
    /// </summary>
    private string? ScrapeCommandLine()
    {
        if (_commandStartLine < 0) return null;
        var last = (int)Math.Max(_commandStartLine, AbsoluteCursorLine);
        var text = GetText((int)_commandStartLine, 0, last, Columns - 1, includeScrollback: true);
        if (string.IsNullOrWhiteSpace(text)) return null;

        // The command is the FIRST prompt-bearing line of the block. Scanning from the end (the
        // old behaviour) returned the last line of OUTPUT, because the range runs to the cursor
        // at block finish: `ls -la /tmp` was recorded as a listing row and `echo x` as `x`.
        var lines = text.Replace("\r", "").Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0) continue;

            var at = -1;
            var skip = 0;
            foreach (var prompt in new[] { "$ ", "# ", "% ", "\u276f ", "> " })
            {
                var index = trimmed.LastIndexOf(prompt, StringComparison.Ordinal);
                if (index > at) { at = index; skip = prompt.Length; }
            }

            if (at >= 0)
            {
                // A prompt with nothing after it: the command is on a following line.
                if (at + skip >= trimmed.Length) continue;
                var rest = trimmed[(at + skip)..].Trim();
                if (rest.Length > 0) return rest;
                continue;
            }

            // No prompt on this line, so this is the command itself (prompt scrolled off, or a
            // wrapped command line). Returning it beats falling through to the output below.
            return trimmed.Trim();
        }
        return null;
    }

    public void FullReset()
    {
        _altActive = false;
        Scrollback.Clear();
        for (var r = 0; r < Rows; r++)
        {
            var line = Screen[r];
            for (var c = 0; c < Columns; c++) line.Cells[c] = Cell.Blank();
            line.Wrapped = false;
            line.Dirty = true;
        }
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        CursorRow = CursorCol = 0;
        CursorVisible = true;
        AutoWrap = true;
        OriginMode = false;
        InsertMode = false;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        MouseMode = 0;
        SgrMouse = false;
        _pen = Cell.Blank();
        Prompts.Clear();
        ResetTabs();
        DirtyAll();
    }

    // ---------------------------------------------------------------- resize

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows) return;

        if (_altActive)
        {
            // Full-screen programs redraw themselves on the new size; trimming is honest.
            Screen = ResizeScreen(Screen, columns, rows);
            _altScreen = ResizeScreen(_altScreen, columns, rows);
            Columns = columns;
            Rows = rows;
            ScrollTop = 0;
            ScrollBottom = Rows - 1;
            CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
            CursorCol = Math.Clamp(CursorCol, 0, Columns - 1);
            DirtyAll();
            return;
        }

        ReflowPrimary(columns, rows);
        _altScreen = ResizeScreen(_altScreen, columns, rows);
        DirtyAll();
    }

    private sealed class ReflowRun
    {
        public readonly List<Cell> Cells = new();
        public int CursorOffset = -1;   // cells before the cursor inside this run
    }

    /// <summary>
    /// Rewraps the primary buffer and scrollback to a new width: soft-wrapped runs are
    /// joined into logical lines and re-split at the new width, so narrowing no longer
    /// chops lines and widening restores them. Characters are never dropped, and a
    /// double-width cell is never cut away from its continuation.
    /// </summary>
    private void ReflowPrimary(int columns, int rows)
    {
        var cursorAbsolute = Scrollback.Count + CursorRow;
        var cursorColumn = CursorCol;
        var screenStartSource = Scrollback.Count;

        var source = new List<TermLine>(Scrollback.Count + Screen.Length);
        source.AddRange(Scrollback);
        source.AddRange(Screen);

        var runs = new List<ReflowRun>();
        var screenRunIndex = -1;
        ReflowRun? current = null;
        var sourceRun = new int[Math.Max(1, source.Count)];
        var sourceOffset = new int[Math.Max(1, source.Count)];

        for (var i = 0; i < source.Count; i++)
        {
            var line = source[i];
            if (current is null || (i > 0 && !source[i - 1].Wrapped))
            {
                current = new ReflowRun();
                runs.Add(current);
                if (screenRunIndex < 0 && i >= screenStartSource) screenRunIndex = runs.Count - 1;
            }

            var length = line.Cells.Length;
            if (!line.Wrapped)
            {
                // Trim padding only on the last physical line of a logical line.
                while (length > 0 && line.Cells[length - 1].Width == 1 && line.Cells[length - 1].Codepoint == ' ')
                    length--;
            }

            var offsetInRun = current!.Cells.Count;
            for (var c = 0; c < length; c++) current.Cells.Add(line.Cells[c]);
            sourceRun[i] = runs.Count - 1;
            sourceOffset[i] = offsetInRun;

            if (i == cursorAbsolute) current.CursorOffset = offsetInRun + Math.Min(cursorColumn, length);
        }

        var emitted = new List<TermLine>();
        var cursorLine = -1;
        var cursorColumnOut = 0;
        var emittedAtScreenStart = -1;
        var runFirstEmitted = new int[Math.Max(1, runs.Count)];
        var runLines = new List<(int Start, int End)>[runs.Count];

        for (var r = 0; r < runs.Count; r++)
        {
            if (r == screenRunIndex) emittedAtScreenStart = emitted.Count;
            runFirstEmitted[r] = emitted.Count;
            runLines[r] = new List<(int Start, int End)>();

            var run = runs[r];
            if (run.Cells.Count == 0)
            {
                emitted.Add(new TermLine(columns));
                runLines[r].Add((0, 0));
                continue;
            }

            var offset = 0;
            while (offset < run.Cells.Count)
            {
                var take = Math.Min(columns, run.Cells.Count - offset);
                if (take > 1 && offset + take < run.Cells.Count && run.Cells[offset + take - 1].Width == 2)
                    take--;

                var line = new TermLine(columns);
                for (var c = 0; c < take && offset + c < run.Cells.Count; c++)
                    line.Cells[c] = run.Cells[offset + c];
                line.Wrapped = offset + take < run.Cells.Count;
                emitted.Add(line);
                runLines[r].Add((offset, offset + take));

                if (run.CursorOffset >= offset && run.CursorOffset <= offset + take)
                {
                    cursorLine = emitted.Count - 1;
                    cursorColumnOut = Math.Min(run.CursorOffset - offset, columns - 1);
                }

                offset += take;
            }
        }

        if (emittedAtScreenStart < 0) emittedAtScreenStart = 0;

        // Anchor on the line that used to be the screen's first row: trailing blank rows
        // are not content. If narrowing pushes the cursor past the last row, scroll just
        // far enough to keep it visible.
        var maxStart = Math.Max(0, emitted.Count - rows);
        var screenStart = Math.Clamp(emittedAtScreenStart, 0, maxStart);
        if (cursorLine >= 0 && cursorLine - screenStart >= rows)
            screenStart = Math.Clamp(cursorLine - (rows - 1), 0, maxStart);

        var backStart = Math.Max(0, screenStart - ScrollbackLimit);

        // Line numbers changed with the rewrap, so shell-integration marks follow their text.
        // Without this a jump-to-prompt lands on whatever line now holds the old number.
        int MapSourceLine(long sourceLine)
        {
            if (source.Count == 0) return 0;
            var index = (int)Math.Clamp(sourceLine, 0, source.Count - 1);
            var runIndex = sourceRun[index];
            var offset = sourceOffset[index];
            var lines = runLines[runIndex];
            if (lines is null || lines.Count == 0) return runFirstEmitted[runIndex];

            var local = lines.Count - 1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (offset >= lines[i].Start && offset <= lines[i].End) { local = i; break; }
            }
            return runFirstEmitted[runIndex] + local;
        }

        foreach (var mark in Prompts)
        {
            mark.Line = MapSourceLine(mark.Line);
            if (mark.OutputLine >= 0) mark.OutputLine = MapSourceLine(mark.OutputLine);
            if (mark.EndLine >= 0) mark.EndLine = MapSourceLine(mark.EndLine);
            if (mark.NextLine >= 0) mark.NextLine = MapSourceLine(mark.NextLine);
        }
        if (_openMark is not null) _openMark.Line = MapSourceLine(_openMark.Line);
        if (_commandStartLine >= 0) _commandStartLine = MapSourceLine(_commandStartLine);
        if (backStart > 0) Prompts.RemoveAll(mark => mark.Line < backStart);

        Scrollback.Clear();
        for (var i = backStart; i < screenStart; i++) Scrollback.Add(emitted[i]);

        Screen = new TermLine[rows];
        for (var i = 0; i < rows; i++)
            Screen[i] = screenStart + i < emitted.Count ? emitted[screenStart + i] : new TermLine(columns);

        Columns = columns;
        Rows = rows;
        ScrollTop = 0;
        ScrollBottom = rows - 1;

        if (cursorLine >= 0)
        {
            CursorRow = Math.Clamp(cursorLine - screenStart, 0, rows - 1);
            CursorCol = Math.Clamp(cursorColumnOut, 0, columns - 1);
        }
        else
        {
            CursorRow = rows - 1;
            CursorCol = Math.Clamp(Math.Min(cursorColumn, columns - 1), 0, columns - 1);
        }
    }

    /// <summary>Trims or pads rows in place; the alternate screen redraws itself.</summary>
    private static TermLine[] ResizeScreen(TermLine[] screen, int columns, int rows)
    {
        var next = new TermLine[rows];
        for (var i = 0; i < rows; i++)
        {
            if (i < screen.Length)
            {
                var line = screen[i];
                line.Resize(columns);
                next[i] = line;
            }
            else
            {
                next[i] = new TermLine(columns);
            }
        }
        return next;
    }
    // ---------------------------------------------------------------- text extraction

    public string GetText(int fromRow, int fromCol, int toRow, int toCol, bool includeScrollback = false)
    {
        if (fromRow > toRow || fromRow == toRow && fromCol > toCol)
            (fromRow, fromCol, toRow, toCol) = (toRow, toCol, fromRow, fromCol);

        var builder = new System.Text.StringBuilder();
        for (var r = fromRow; r <= toRow; r++)
        {
            var line = includeScrollback ? GetAnyLine(r) : ActiveLine(Math.Clamp(r, 0, Rows - 1));
            var start = r == fromRow ? fromCol : 0;
            var end = r == toRow ? toCol : Columns - 1;
            start = Math.Clamp(start, 0, Columns - 1);
            end = Math.Clamp(end, 0, Columns - 1);
            var lineBuilder = new System.Text.StringBuilder();
            for (var c = start; c <= end; c++)
            {
                var cell = line.Cells[c];
                if (cell.Width == 0) continue;
                if (cell.Codepoint <= 0) lineBuilder.Append(' ');
                else lineBuilder.Append(char.ConvertFromUtf32(cell.Codepoint));
            }
            builder.Append(lineBuilder.ToString().TrimEnd());
            if (r != toRow) builder.AppendLine();
        }
        return builder.ToString();
    }

    private TermLine GetAnyLine(long index)
    {
        if (index < 0)
        {
            var i = Scrollback.Count + index;
            return i >= 0 && i < Scrollback.Count ? Scrollback[(int)i] : new TermLine(Columns);
        }
        return ActiveLine((int)Math.Clamp(index, 0, Rows - 1));
    }

    public string GetRowText(int row)
    {
        return LineText(ActiveLine(Math.Clamp(row, 0, Rows - 1)));
    }

    /// <summary>
    /// The tail of what the screen and scrollback hold, newest last. A failed connection already
    /// wrote ssh's reason into the buffer, so this gives the Diagnose window the text to explain
    /// without re-running anything — and it still works when the failure was local (no output).
    /// </summary>
    public string Tail(int lines)
    {
        var picked = new List<string>();
        for (var row = Rows - 1; row >= 0 && picked.Count < lines; row--)
            picked.Add(GetRowText(row));
        for (var i = Scrollback.Count - 1; i >= 0 && picked.Count < lines; i--)
            picked.Add(LineText(Scrollback[i]));

        picked.Reverse();
        while (picked.Count > 0 && picked[0].Trim().Length == 0) picked.RemoveAt(0);
        return string.Join("\n", picked);
    }

    private static string LineText(TermLine line)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var cell in line.Cells)
        {
            if (cell.Width == 0) continue;
            builder.Append(cell.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
        }
        return builder.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- width

    public static int CharWidth(int cp)
    {
        if (cp == 0) return 0;
        if (cp < 32) return 0;
        if (cp < 0x1100) return 1;

        // Combining marks.
        var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(cp);
        if (category is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.Format)
            return 0;

        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) => cp switch
    {
        >= 0x1100 and <= 0x115F => true,
        >= 0x2E80 and <= 0xA4CF => true,
        >= 0xAC00 and <= 0xD7A3 => true,
        >= 0xF900 and <= 0xFAFF => true,
        >= 0xFE30 and <= 0xFE6F => true,
        >= 0xFF00 and <= 0xFF60 => true,
        >= 0xFFE0 and <= 0xFFE6 => true,
        >= 0x1F300 and <= 0x1F64F => true,
        >= 0x1F900 and <= 0x1F9FF => true,
        >= 0x1F680 and <= 0x1F6FF => true,
        >= 0x20000 and <= 0x3FFFD => true,
        _ => false
    };
}

/// <summary>OSC 133 prompt mark: one command block.</summary>
public sealed class PromptMark
{
    /// <summary>
    /// Monotonic block number, assigned when the block finishes. Line numbers move when scrollback
    /// is trimmed or the window resizes; a seq does not, so an observer can say "everything after 41".
    /// </summary>
    public long Seq { get; set; }

    /// <summary>Absolute line of the command (where OSC 133;B arrived).</summary>
    public long Line { get; set; }

    /// <summary>Absolute line where output began (OSC 133;C).</summary>
    public long OutputLine { get; set; } = -1;

    /// <summary>Absolute line where the shell reported the exit status (OSC 133;D).</summary>
    public long EndLine { get; set; } = -1;

    public int ExitCode { get; set; } = -1;
    public DateTime Started { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Command { get; set; }

    /// <summary>Absolute line of the NEXT block's prompt (its OSC 133;A), or -1 while this is
    /// the last block. Fish's D arrives on the row BELOW its output and that same row carries
    /// the next prompt, so the block's text must stop above it — otherwise a receipt written
    /// after the prompt was painted ends with the next prompt line.</summary>
    public long NextLine { get; set; } = -1;

    /// <summary>Working directory at the time the command started, when the shell reported it.</summary>
    public string? Cwd { get; set; }
}
