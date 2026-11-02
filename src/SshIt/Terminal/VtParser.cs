using System.Text;

namespace SshIt.Terminal;

/// <summary>
/// Incremental VT100/xterm parser. Bytes in, terminal state out.
/// Covers the sequences real shells, tmux, vim, htop and friends actually emit:
/// full SGR (16/256/true colour), scroll regions, alternate screen, insert/delete,
/// cursor save/restore, DEC private modes, OSC titles, working-directory and
/// shell-integration marks.
/// </summary>
public sealed class VtParser
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        CsiIgnore,
        Osc,
        Dcs,
        Charset,
        Hash
    }

    private readonly TerminalBuffer _buf;
    private readonly Action<byte[]> _respond;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _chars = new char[16384];

    private State _state = State.Ground;
    private char _prefix;
    private readonly List<int> _params = new(16);
    private readonly List<char> _intermediates = new(4);
    private int _current;
    private bool _hasCurrent;
    private readonly StringBuilder _osc = new();
    private bool _oscEscaped;
    private char _highSurrogate;
    private int _charsetIndex;

    public VtParser(TerminalBuffer buffer, Action<byte[]> respond)
    {
        _buf = buffer;
        _respond = respond;
    }

    public void Feed(byte[] data, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            _decoder.Convert(data, offset, count - offset, _chars, 0, _chars.Length, false,
                             out var bytesUsed, out var charsUsed, out _);
            offset += bytesUsed;
            Process(_chars, charsUsed);
            if (bytesUsed == 0 && charsUsed == 0) break;
        }
    }

    public void Feed(byte[] data) => Feed(data, data.Length);

    private void Process(char[] chars, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var ch = chars[i];

            // ESC always starts a new sequence, and CAN/SUB abort one, in any state.
            if (ch == '\u001b')
            {
                FlushOscOrDcs();
                _state = State.Escape;
                ResetSequence();
                continue;
            }
            if (ch == '\u0018' || ch == '\u001a')
            {
                _state = State.Ground;
                continue;
            }

            switch (_state)
            {
                case State.Ground:
                    Ground(ch);
                    break;
                case State.Escape:
                    Escape(ch);
                    break;
                case State.EscapeIntermediate:
                    _intermediates.Add(ch);
                    _state = State.Ground;
                    break;
                case State.Csi:
                    Csi(ch);
                    break;
                case State.CsiIgnore:
                    if (ch >= '@' && ch <= '~') _state = State.Ground;
                    break;
                case State.Osc:
                    Osc(ch);
                    break;
                case State.Dcs:
                    if (ch == '\u0007') _state = State.Ground;
                    break;
                case State.Charset:
                    _buf.SetCharacterSet(_charsetIndex, ch);
                    _state = State.Ground;
                    break;
                case State.Hash:
                    if (ch == '8')
                    {
                        // DECALN: fill the screen with E, used by terminal tests.
                        for (var r = 0; r < _buf.Rows; r++)
                        {
                            _buf.SetCursor(r, 0);
                            for (var c = 0; c < _buf.Columns; c++) _buf.Write('E');
                        }
                        _buf.SetCursor(0, 0);
                    }
                    _state = State.Ground;
                    break;
            }
        }
    }

    private void ResetSequence()
    {
        _params.Clear();
        _intermediates.Clear();
        _current = 0;
        _hasCurrent = false;
        _prefix = '\0';
        _osc.Clear();
        _oscEscaped = false;
    }

    private void Ground(char ch)
    {
        if (ch < ' ')
        {
            switch (ch)
            {
                case '\u0007': _buf.Bell(); break;
                case '\b': _buf.Backspace(); break;
                case '\t': _buf.Tab(); break;
                case '\n': case '\u000b': case '\f': _buf.LineFeed(); break;
                case '\r': _buf.CarriageReturn(); break;
                case '\u000e': _buf.SelectCharacterSet(g1: true); break;
                case '\u000f': _buf.SelectCharacterSet(g1: false); break;
            }
            return;
        }

        if (ch == '\u007f') return; // DEL

        if (char.IsHighSurrogate(ch)) { _highSurrogate = ch; return; }
        if (char.IsLowSurrogate(ch))
        {
            if (_highSurrogate != 0)
            {
                _buf.Write(char.ConvertToUtf32(_highSurrogate, ch));
                _highSurrogate = '\0';
            }
            return;
        }
        _highSurrogate = '\0';
        _buf.Write(ch);
    }

    private void Escape(char ch)
    {
        switch (ch)
        {
            case '[':
                _state = State.Csi;
                ResetSequence();
                return;
            case ']':
                _state = State.Osc;
                ResetSequence();
                return;
            case 'P':
                _state = State.Dcs;
                return;
            case '(': _charsetIndex = 0; _state = State.Charset; return;
            case ')': _charsetIndex = 1; _state = State.Charset; return;
            case '*': case '+': _charsetIndex = 2; _state = State.Charset; return;
            case '#':
                _state = State.Hash;
                return;
            case '7': _buf.SaveCursor(); _state = State.Ground; return;
            case '8': _buf.RestoreCursor(); _state = State.Ground; return;
            case 'D': _buf.LineFeed(); _state = State.Ground; return;
            case 'E': _buf.CarriageReturn(); _buf.LineFeed(); _state = State.Ground; return;
            case 'M': _buf.ReverseIndex(); _state = State.Ground; return;
            case 'H': _buf.SetTabStop(); _state = State.Ground; return;
            case 'c': _buf.FullReset(); _state = State.Ground; return;
            case '=': _buf.SetMode(66, true); _state = State.Ground; return;
            case '>': _buf.SetMode(66, false); _state = State.Ground; return;
            case '\\': _state = State.Ground; return;
            default:
                _state = ch is >= ' ' and <= '/' ? State.EscapeIntermediate : State.Ground;
                return;
        }
    }

    private void Csi(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            _current = Math.Min(_current * 10 + (ch - '0'), 65535);
            _hasCurrent = true;
            return;
        }

        if (ch == ';')
        {
            PushParam();
            return;
        }

        if (ch == ':')
        {
            // Sub-parameter separator: an empty sub-parameter is meaningful
            // for 38:2::r:g:b, so record it as -1.
            _params.Add(_hasCurrent ? _current : -1);
            _current = 0;
            _hasCurrent = false;
            return;
        }

        if (ch is '?' or '>' or '<' or '=' or '!')
        {
            _prefix = ch;
            return;
        }

        if (ch is >= ' ' and <= '/')
        {
            _intermediates.Add(ch);
            return;
        }

        if (ch >= '@' && ch <= '~')
        {
            PushParam();
            Dispatch(ch);
            _state = State.Ground;
            return;
        }

        _state = State.CsiIgnore;
    }

    private void PushParam()
    {
        _params.Add(_hasCurrent ? _current : 0);
        _current = 0;
        _hasCurrent = false;
    }

    private int Param(int index, int fallback = 0)
    {
        if (index >= _params.Count) return fallback;
        var v = _params[index];
        if (v < 0) return fallback;
        return v == 0 && fallback > 0 ? fallback : v;
    }

    private void Dispatch(char final)
    {
        var p0 = Param(0);
        var p1 = Param(1);

        switch (final)
        {
            case 'A': _buf.MoveCursor(-Math.Max(1, p0), 0); break;
            case 'B': _buf.MoveCursor(Math.Max(1, p0), 0); break;
            case 'C': _buf.MoveCursor(0, Math.Max(1, p0)); break;
            case 'D': _buf.MoveCursor(0, -Math.Max(1, p0)); break;
            case 'E': _buf.MoveCursor(Math.Max(1, p0), 0); _buf.CarriageReturn(); break;
            case 'F': _buf.MoveCursor(-Math.Max(1, p0), 0); _buf.CarriageReturn(); break;
            case 'G': case '`': _buf.SetCursor(_buf.CursorRow, Math.Max(1, p0) - 1); break;
            case 'H': case 'f': _buf.SetCursor(Math.Max(1, p0) - 1, Math.Max(1, p1) - 1); break;
            case 'I': _buf.Tab(Math.Max(1, p0)); break;
            case 'J': _buf.EraseInDisplay(p0); break;
            case 'K': _buf.EraseInLine(p0); break;
            case 'L': _buf.InsertLines(Math.Max(1, p0)); break;
            case 'M': _buf.DeleteLines(Math.Max(1, p0)); break;
            case 'P': _buf.DeleteChars(Math.Max(1, p0)); break;
            case 'S': _buf.ScrollUp(Math.Max(1, p0)); break;
            case 'T': _buf.ScrollDown(Math.Max(1, p0)); break;
            case 'X': _buf.EraseChars(Math.Max(1, p0)); break;
            case 'Z': _buf.BackTab(Math.Max(1, p0)); break;
            case 'a': _buf.MoveCursor(0, Math.Max(1, p0)); break;
            case 'd': _buf.SetCursor(Math.Max(1, p0) - 1, _buf.CursorCol); break;
            case 'e': _buf.MoveCursor(Math.Max(1, p0), 0); break;
            case 'g': _buf.ClearTabStop(p0); break;
            case 'm': if (_prefix == '\0') _buf.Sgr(_params); break;  // CSI > 4 ; 1 m is modifyOtherKeys
            case 'r':
                _buf.SetScrollRegion(Math.Max(1, p0) - 1, Math.Max(1, p1) - 1);
                break;
            // A private prefix is a different command, not a legacy one with decoration.
            // fish 4 (kitty keyboard protocol) sends CSI = 5 u after enabling a mode and
            // CSI = 0 u at the end of the redraw; dispatching 'u' as RestoreCursor yanked
            // the cursor back to the saved (0,0) in the middle of the command line, which
            // put the command, the output and the marks on the banner row. xterm's
            // CSI > 4 ; 1 m is modifyOtherKeys, not an SGR that bolds the typed command.
            case 's': if (_prefix == '\0') _buf.SaveCursor(); break;
            case 'u': if (_prefix == '\0') _buf.RestoreCursor(); break;
            case 'h': SetModes(true); break;
            case 'l': SetModes(false); break;
            case 'n': DeviceStatus(p0); break;
            case 'c': DeviceAttributes(); break;
            case 'q':
                if (_intermediates.Contains(' ')) _buf.SetCursorShape(p0, p0 != 1 && p0 != 3 && p0 != 5);
                break;
            case 't': WindowOperation(p0, p1); break;
            case 'Q': break;
            case 'p': if (_intermediates.Contains('!')) _buf.FullReset(); break;
            case '@': _buf.InsertChars(Math.Max(1, p0)); break;
        }
    }

    private void SetModes(bool on)
    {
        if (_prefix == '?')
        {
            foreach (var p in _params)
                if (p > 0) _buf.SetMode(p, on);
        }
        else
        {
            foreach (var p in _params)
                _buf.SetMode(p, on);
        }
    }

    private void DeviceStatus(int mode)
    {
        switch (mode)
        {
            case 5:
                Respond("\u001b[0n");
                break;
            case 6:
                Respond($"\u001b[{_buf.CursorRow + 1};{_buf.CursorCol + 1}R");
                break;
        }
    }

    private void DeviceAttributes()
    {
        if (_prefix == '>') Respond("\u001b[>0;10;1c");
        else Respond("\u001b[?1;2c");
    }

    private void WindowOperation(int op, int p1)
    {
        switch (op)
        {
            case 18:
                Respond($"\u001b[8;{_buf.Rows};{_buf.Columns}t");
                break;
        }
    }

    private void Respond(string text) => _respond(Encoding.UTF8.GetBytes(text));

    // ---------------------------------------------------------------- OSC

    private void Osc(char ch)
    {
        if (ch == '\u0007')
        {
            FlushOscOrDcs();
            _state = State.Ground;
            return;
        }
        if (_oscEscaped)
        {
            if (ch == '\\') { FlushOscOrDcs(); _state = State.Ground; return; }
            _oscEscaped = false;
        }
        if (ch == '\u001b') { _oscEscaped = true; return; }
        if (_osc.Length < 8192) _osc.Append(ch);
    }

    private void FlushOscOrDcs()
    {
        if (_state != State.Osc || _osc.Length == 0) return;

        var text = _osc.ToString();
        var semi = text.IndexOf(';');
        var code = semi < 0 ? text : text[..semi];
        var payload = semi < 0 ? "" : text[(semi + 1)..];

        switch (code)
        {
            case "0":
            case "1":
            case "2":
                _buf.SetTitle(Unescape(payload));
                break;
            case "7":
                var cwd = ParseFileUri(payload);
                if (!string.IsNullOrEmpty(cwd)) _buf.SetWorkingDirectory(cwd);
                break;
            case "9":
                // iTerm2-style notification; surfaced by the shell wrapper.
                break;
            case "133":
                HandleShellIntegration(payload);
                break;
            case "633":
                // VS Code shell integration. E;<cmdline>;<nonce> is the exact command
                // line; the nonce is reserved for provenance checks.
                if (payload.StartsWith("E;", StringComparison.Ordinal))
                {
                    var body = payload[2..];
                    var nonceAt = body.LastIndexOf(';');
                    _buf.SetCommandLine(nonceAt > 0 ? body[..nonceAt] : body);
                }
                break;
        }
        _osc.Clear();
    }

    private void HandleShellIntegration(string payload)
    {
        var parts = payload.Split(';');
        var kind = parts.Length > 0 ? parts[0] : "";
        switch (kind)
        {
            case "A":
                _buf.MarkPromptStart();
                break;
            case "B":
                _buf.MarkCommandStart();
                break;
            case "C":
                // fish 4 sends the exact command with the output mark: C;cmdline_url=echo%20hi.
                // Use it instead of scraping the prompt line.
                if (parts.Length > 1 && parts[1].StartsWith("cmdline_url=", StringComparison.Ordinal))
                {
                    var exact = System.Uri.UnescapeDataString(parts[1]["cmdline_url=".Length..]);
                    if (exact.Length > 0) _buf.SetCommandLine(exact);
                }
                _buf.MarkOutputStart();
                break;
            case "D":
                var exit = parts.Length > 1 && int.TryParse(parts[1], out var e) ? e : -1;
                _buf.MarkCommandFinished(exit);
                break;
        }
    }

    private static string ParseFileUri(string uri)
    {
        try
        {
            if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return uri;
            var rest = uri.Substring(7);
            var slash = rest.IndexOf('/');
            var path = slash >= 0 ? rest[slash..] : rest;
            return Uri.UnescapeDataString(path);
        }
        catch { return uri; }
    }

    private static string Unescape(string text) => text.Replace("\u001b", "");
}
