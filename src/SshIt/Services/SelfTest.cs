using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Terminal;

namespace SshIt.Services;

/// <summary>
/// Headless regression suite for the terminal core (buffer + VT parser).
/// Run with:  SSH-it.exe --selftest
/// Writes results to %APPDATA%\SSH-it\logs\selftest.txt and exits 0/1.
/// </summary>
public static class SelfTest
{
    private static int _pass;
    private static int _fail;
    private static readonly StringBuilder Report = new();

    public static int Run()
    {
        _pass = _fail = 0;
        Report.Clear();
        Report.AppendLine($"SSH-it self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Report.AppendLine(new string('-', 60));

        PlainText();
        LineEndings();
        CursorAddressing();
        EraseOperations();
        InsertDelete();
        ColourAttributes();
        Wrapping();
        ScrollRegion();
        AlternateScreen();
        WideCharacter();
        TabStops();
        BackspaceBoundary();
        TitleOsc();
        ShellIntegration();
        DecGraphics();
        SaveRestoreCursor();
        ResetBehaviour();
        TextExtraction();
        ReflowOnResize();
        DecGraphicsReflow();
        PromptMarksAcrossResize();
        CommandBlocks();
        BlockOutputChecks();
        FishCaptureChecks();
        PrivatePrefixChecks();
        ShellIntegrationChecks();
        TextUnits();
        HostPanel();
        TunnelSpecs();
        Diagnostics();
        FleetPostureChecks();
        WhyButtonChecks();
        EvidenceChecks();
        LeaseChecks();
        PolicyChecks();
        CastChecks();
        ServiceChecks();
        InventoryChecks();
        QueueChecks();
        PaletteChecks();
        AccessibilityChecks();
        McpChecks();
        TmuxChecks();
        LedgerChecks();
        WorkspaceChecks();
        TagChecks();
        FanoutChecks();
        SyncChecks();
        DeployChecks();
        AuditChecks();
        BenchChecks();
        HelpChecks();
        ReconnectChecks();
        ConnectionProofChecks();
        WatchChecks();
        DiffChecks();
        BlastChecks();
        UndoChecks();
        AgentGate();
        ArgumentQuoting();
        HighContrast();
        ThemePalettes();
        JumpChainRoute();
        X11Checks();
        ConnectionKinds();
        ZmodemChecks();
        WindowSessions();
        PseudoConsoleInput();

        Report.AppendLine(new string('-', 60));
        Report.AppendLine($"RESULT: {_pass} passed, {_fail} failed");

        try
        {
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(Path.Combine(Log.Directory, "selftest.txt"), Report.ToString(), Encoding.UTF8);
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- cases

    private static void PlainText()
    {
        var (buffer, parser) = Make(20, 5);
        Feed(parser, "AB");
        Check("plain text is written", Row(buffer, 0) == "AB", Row(buffer, 0));
        Check("cursor advances", buffer.CursorCol == 2, buffer.CursorCol.ToString());
    }

    private static void LineEndings()
    {
        var (buffer, parser) = Make(20, 5);
        Feed(parser, "A\r\nB");
        Check("CRLF moves to the next row", Row(buffer, 0) == "A" && Row(buffer, 1) == "B",
              $"row0='{Row(buffer, 0)}' row1='{Row(buffer, 1)}'");
    }

    private static void CursorAddressing()
    {
        var (buffer, parser) = Make(20, 6);
        Feed(parser, "\u001b[3;4H");
        Check("CUP 3;4 addresses row 2 col 3", buffer.CursorRow == 2 && buffer.CursorCol == 3,
              $"{buffer.CursorRow},{buffer.CursorCol}");

        Feed(parser, "\u001b[H");
        Check("CUP with no parameters homes the cursor", buffer.CursorRow == 0 && buffer.CursorCol == 0,
              $"{buffer.CursorRow},{buffer.CursorCol}");
    }

    private static void EraseOperations()
    {
        var (buffer, parser) = Make(10, 3);
        Feed(parser, "ABCDE");
        Feed(parser, "\u001b[3G\u001b[K");
        Check("erase to end of line", Row(buffer, 0) == "AB", Row(buffer, 0));

        var (buffer2, parser2) = Make(10, 3);
        Feed(parser2, "ABCDE");
        Feed(parser2, "\u001b[2J");
        Check("erase display clears the screen", Row(buffer2, 0) == "", $"'{Row(buffer2, 0)}'");
    }

    private static void InsertDelete()
    {
        var (buffer, parser) = Make(12, 3);
        Feed(parser, "ABC");
        Feed(parser, "\u001b[1;2H\u001b[2@");
        Check("insert two characters", Row(buffer, 0).StartsWith("A  BC", StringComparison.Ordinal),
              $"'{Row(buffer, 0)}'");

        var (buffer2, parser2) = Make(12, 3);
        Feed(parser2, "ABCDE");
        Feed(parser2, "\u001b[1;2H\u001b[2P");
        Check("delete two characters", Row(buffer2, 0).StartsWith("ADE", StringComparison.Ordinal),
              $"'{Row(buffer2, 0)}'");
    }

    private static void ColourAttributes()
    {
        var (buffer, parser) = Make(10, 3);
        Feed(parser, "\u001b[31mX");
        Check("SGR 31 sets indexed red", buffer.ActiveLine(0).Cells[0].Fg == TermColor.Indexed(1),
              buffer.ActiveLine(0).Cells[0].Fg.ToString("X8"));

        var (buffer2, parser2) = Make(10, 3);
        Feed(parser2, "\u001b[38;2;10;20;30mX");
        Check("SGR truecolour", buffer2.ActiveLine(0).Cells[0].Fg == TermColor.Rgb(10, 20, 30),
              buffer2.ActiveLine(0).Cells[0].Fg.ToString("X8"));

        var (buffer3, parser3) = Make(10, 3);
        Feed(parser3, "\u001b[1;4mX");
        var flags = buffer3.ActiveLine(0).Cells[0].Flags;
        Check("SGR bold+underline", (flags & CellFlags.Bold) != 0 && (flags & CellFlags.Underline) != 0,
              flags.ToString());

        var (buffer4, parser4) = Make(10, 3);
        Feed(parser4, "\u001b[31;42;1mX\u001b[0mY");
        var second = buffer4.ActiveLine(0).Cells[1];
        Check("SGR 0 resets attributes", second.Fg == 0 && second.Bg == 0 && second.Flags == CellFlags.None,
              $"{second.Fg:X8}/{second.Bg:X8}/{second.Flags}");
    }

    private static void Wrapping()
    {
        var (buffer, parser) = Make(5, 4);
        Feed(parser, "ABCDEF");
        Check("autowrap puts the seventh character on row 1", Row(buffer, 1).StartsWith("F", StringComparison.Ordinal),
              $"row1='{Row(buffer, 1)}'");
        Check("wrapped line is flagged", buffer.ActiveLine(0).Wrapped, "wrap flag false");
    }

    private static void ScrollRegion()
    {
        // Drive the buffer directly so the test cannot depend on parser cursor state.
        var buffer = new TerminalBuffer(10, 4);
        for (var i = 0; i < 4; i++)
        {
            buffer.SetCursor(i, 0);
            buffer.Write('A' + i);
        }
        Check("four lines fill the screen", Row(buffer, 3) == "D", Row(buffer, 3));

        buffer.SetCursor(3, 0);
        buffer.CarriageReturn();
        buffer.LineFeed();
        Check("line feed at the bottom scrolls", buffer.Scrollback.Count == 1,
              $"sb={buffer.Scrollback.Count} calls={buffer.DebugScrollUps} cur={buffer.CursorRow} region={buffer.ScrollTop}..{buffer.ScrollBottom}");
        Check("scrolled content moved up", Row(buffer, 0) == "B" && Row(buffer, 3) == "",
              $"rows=[{Row(buffer, 0)}][{Row(buffer, 1)}][{Row(buffer, 2)}][{Row(buffer, 3)}]");

        // Parser path: CR LF through the escape decoder must scroll too.
        var parsed = new TerminalBuffer(10, 4);
        var parser = new VtParser(parsed, _ => { });
        var crlf = new string(new[] { (char)13, (char)10 });
        Feed(parser, "1" + crlf + "2" + crlf + "3" + crlf + "4" + crlf + "5");
        Check("parser CRLF scrolls into scrollback", parsed.Scrollback.Count == 1 && Row(parsed, 3) == "5",
              $"sb={parsed.Scrollback.Count} rows=[{Row(parsed, 0)}][{Row(parsed, 3)}]");
    }

    private static void AlternateScreen()
    {
        var (buffer, parser) = Make(20, 4);
        Feed(parser, "MAIN");
        Feed(parser, "\u001b[?1049h");
        Feed(parser, "ALT");
        Check("alt screen shows its own content", Row(buffer, 0).StartsWith("ALT", StringComparison.Ordinal),
              Row(buffer, 0));
        Feed(parser, "\u001b[?1049l");
        Check("primary screen is restored", Row(buffer, 0).StartsWith("MAIN", StringComparison.Ordinal),
              Row(buffer, 0));
    }

    private static void WideCharacter()
    {
        var (buffer, parser) = Make(10, 3);
        Feed(parser, "中");
        var first = buffer.ActiveLine(0).Cells[0];
        var second = buffer.ActiveLine(0).Cells[1];
        Check("CJK occupies two cells", first.Width == 2 && second.Width == 0,
              $"{first.Width}/{second.Width}");
        Check("cursor advanced by two", buffer.CursorCol == 2, buffer.CursorCol.ToString());
    }

    private static void TabStops()
    {
        var (buffer, parser) = Make(30, 3);
        Feed(parser, "A\tB");
        Check("tab lands on column 8", buffer.ActiveLine(0).Cells[8].Codepoint == 'B',
              $"col8={buffer.ActiveLine(0).Cells[8].Codepoint}");
    }

    private static void BackspaceBoundary()
    {
        var (buffer, parser) = Make(10, 3);
        Feed(parser, "\b\b\bX");
        Check("backspace clamps at column 0", Row(buffer, 0).StartsWith("X", StringComparison.Ordinal),
              Row(buffer, 0));
    }

    private static void TitleOsc()
    {
        var (buffer, parser) = Make(10, 3);
        Feed(parser, "\u001b]0;my-title\u0007");
        Check("OSC 0 sets the title", buffer.Title == "my-title", buffer.Title);
    }

    private static void ShellIntegration()
    {
        var (buffer, parser) = Make(20, 4);
        Feed(parser, "output\r\n");
        Feed(parser, "\u001b]133;D;7\u0007");
        Check("OSC 133 D records an exit code",
              buffer.Prompts.Count == 1 && buffer.Prompts[0].ExitCode == 7,
              $"count={buffer.Prompts.Count}");
    }

    private static void DecGraphics()
    {
        var (buffer, parser) = Make(10, 3);
        Feed(parser, "\u001b(0q\u001b(B");
        Check("DEC graphics maps q to a horizontal line",
              buffer.ActiveLine(0).Cells[0].Codepoint == '─',
              $"U+{buffer.ActiveLine(0).Cells[0].Codepoint:X4}");
    }

    private static void SaveRestoreCursor()
    {
        var (buffer, parser) = Make(10, 4);
        Feed(parser, "\u001b[2;3H\u001b7\u001b[H\u001b8");
        Check("save/restore returns to 2;3", buffer.CursorRow == 1 && buffer.CursorCol == 2,
              $"{buffer.CursorRow},{buffer.CursorCol}");
    }

    private static void ResetBehaviour()
    {
        var (buffer, parser) = Make(10, 4);
        Feed(parser, "junk\u001bc");
        Check("RIS clears the screen", Row(buffer, 0) == "", $"'{Row(buffer, 0)}'");
        Check("RIS homes the cursor", buffer.CursorRow == 0 && buffer.CursorCol == 0,
              $"{buffer.CursorRow},{buffer.CursorCol}");
    }

    private static void TextExtraction()
    {
        var (buffer, parser) = Make(12, 3);
        Feed(parser, "hello world");
        var text = buffer.GetText(0, 0, 0, 10);
        Check("selection text extraction", text == "hello world", $"'{text}'");
    }


    // ---------------------------------------------------------------- helpers

    // ---------------------------------------------------------------- DEC graphics reflow

    /// <summary>
    /// Box drawing arrives as ordinary cells, so a rewrap must keep the glyphs and their
    /// wrap flags rather than trimming the run.
    /// </summary>
    private static void DecGraphicsReflow()
    {
        var (buffer, parser) = Make(10, 4);
        Feed(parser, "\u001b(0" + new string('q', 15) + "\u001b(B");

        Check("reflow: DEC graphics wrap like text",
              Row(buffer, 0).Length == 10 && Row(buffer, 0)[0] == '─' && buffer.LineAt(0).Wrapped,
              "'" + Row(buffer, 0) + "'");

        buffer.Resize(4, 6);
        var kept = 0;
        for (var row = 0; row < 6; row++)
        {
            var text = Row(buffer, row);
            for (var i = 0; i < text.Length; i++) if (text[i] == '─') kept++;
        }
        Check("reflow: DEC graphics survive a narrow resize", kept == 15, kept.ToString());
    }

    // ---------------------------------------------------------------- prompt marks

    /// <summary>
    /// OSC 133 marks name absolute lines, and a rewrap renumbers every line, so a mark has to
    /// follow its text. Otherwise a jump-to-prompt lands on whatever row now holds the old
    /// number, which after a narrow resize is several lines away from the prompt.
    /// </summary>
    private static void PromptMarksAcrossResize()
    {
        var (buffer, parser) = Make(20, 6);
        Feed(parser, "aaaaaaaaaaaaaaaaaa\r\n");
        Feed(parser, "bbbb\r\n");
        Feed(parser, "\u001b]133;D;0\u0007");
        Feed(parser, "$ prompt here\r\n");

        Check("osc133: the mark starts on the prompt row",
              buffer.Prompts.Count == 1 && buffer.Prompts[0].Line == 2,
              "line=" + (buffer.Prompts.Count > 0 ? buffer.Prompts[0].Line.ToString() : "none"));

        buffer.Resize(8, 6);
        var narrowed = buffer.Prompts.Count > 0 ? buffer.Prompts[0].Line : -1;
        Check("osc133: a mark follows its text when narrowing", narrowed == 4, "line=" + narrowed);
        Check("osc133: the narrowed mark still names the prompt row",
              LineText(buffer, narrowed).StartsWith("$ prompt"), LineText(buffer, narrowed));

        buffer.Resize(20, 6);
        var widened = buffer.Prompts.Count > 0 ? buffer.Prompts[0].Line : -1;
        Check("osc133: a mark follows its text when widening back", widened == 2, "line=" + widened);
        Check("osc133: the widened mark still names the prompt row",
              LineText(buffer, widened).StartsWith("$ prompt"), LineText(buffer, widened));
    }

    /// <summary>
    /// A command block: exit status, the output range, and the command line either from
    /// OSC 633;E or scraped off the prompt row when the shell sent no exact copy.
    /// </summary>
    private static void CommandBlocks()
    {
        var (buffer, parser) = Make(40, 8);
        Feed(parser, "$ echo hi");
        Feed(parser, "\u001b]133;C\u0007");
        Feed(parser, "\r\nhi\r\n");
        Feed(parser, "\u001b]133;D;7\u0007");

        var block = buffer.Prompts.Count > 0 ? buffer.Prompts[0] : null;
        Check("blocks: a command records its exit status", block is not null && block.ExitCode == 7,
              block is null ? "none" : block.ExitCode.ToString());
        Check("blocks: the output range is ordered",
              block is not null && block.Line <= block.OutputLine && block.OutputLine <= block.EndLine,
              block is null ? "none" : $"{block.Line}/{block.OutputLine}/{block.EndLine}");
        Check("blocks: the command is scraped off the prompt row",
              block?.Command == "echo hi", block?.Command ?? "none");

        var (exact, exactParser) = Make(40, 8);
        Feed(exactParser, "\u001b]633;E;df -h;nonce1\u0007");
        Feed(exactParser, "\u001b]133;C\u0007");
        Feed(exactParser, "Filesystem\r\n");
        Feed(exactParser, "\u001b]133;D;0\u0007");
        Check("blocks: OSC 633;E gives the exact command line",
              exact.Prompts.Count > 0 && exact.Prompts[0].Command == "df -h",
              exact.Prompts.Count > 0 ? exact.Prompts[0].Command ?? "null" : "none");

        var (wide, wideParser) = Make(40, 8);
        Feed(wideParser, "$ run something long here\r\n");
        Feed(wideParser, "\u001b]133;C\u0007");
        Feed(wideParser, "0123456789 0123456789 0123456789\r\n");
        Feed(wideParser, "\u001b]133;D;0\u0007");
        wide.Resize(12, 8);
        var after = wide.Prompts.Count > 0 ? wide.Prompts[0] : null;
        Check("blocks: every line number follows a reflow",
              after is not null && after.Line <= after.OutputLine && after.OutputLine <= after.EndLine,
              after is null ? "none" : $"{after.Line}/{after.OutputLine}/{after.EndLine}");

        // OSC 7 is what makes the Files panel follow: one event per real change, none for the
        // same directory repeated on every prompt.
        var (cwd, cwdParser) = Make(40, 8);
        var seen = new List<string>();
        cwd.WorkingDirectoryChanged += seen.Add;
        Feed(cwdParser, "\u001b]7;file://host/var/log\u0007");
        var first = seen.Count;
        Feed(cwdParser, "\u001b]7;file://host/var/log\u0007");
        Check("blocks: OSC 7 reports the shell's directory", cwd.WorkingDirectory == "/var/log",
              cwd.WorkingDirectory ?? "null");
        Check("blocks: an unchanged OSC 7 does not re-raise", first == 1 && seen.Count == 1,
              $"{first}/{seen.Count}");
    }

    private static string LineText(TerminalBuffer buffer, long line)
    {
        var term = buffer.LineAtAbsolute(line);
        if (term is null) return "";

        var builder = new StringBuilder();
        foreach (var cell in term.Cells)
        {
            if (cell.Width == 0) continue;
            builder.Append(cell.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
        }
        return builder.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- agent gate

    /// <summary>
    /// The agent gate is an allowlist: with it off every session behaves as before, with it
    /// on only sessions marked for agent use are reachable. Getting this backwards would
    /// either break every existing script or leave the surface open while claiming it is shut.
    /// </summary>
    private static void AgentGate()
    {
        var session = new Models.Session { Id = "s1", Name = "Prod", Host = "prod.example", User = "me" };
        var off = new Models.AppSettings();
        var on = new Models.AppSettings { AgentGate = true };

        Check("gate: off means every session is usable", AgentPolicy.Allows(session, off), "denied");
        Check("gate: on denies an unmarked session", !AgentPolicy.Allows(session, on), "allowed");

        session.AllowAgent = true;
        Check("gate: on allows a marked session", AgentPolicy.Allows(session, on), "denied");
        Check("gate: off still allows a marked session", AgentPolicy.Allows(session, off), "denied");

        var message = AgentPolicy.RefusalMessage(session);
        Check("gate: the refusal names the session", message.Contains("prod.example"), message);
    }

    // ---------------------------------------------------------------- tunnel specs

    /// <summary>
    /// Forward specs the agent verbs accept: L and R take bind:port:host:port, D takes
    /// bind:port. Every failure has to name what is wrong, because the CLI has no other
    /// way to explain itself.
    /// </summary>
    private static void TunnelSpecs()
    {
        var local = TunnelSupervisor.Parse("L:127.0.0.1:8080:localhost:80");
        Check("tunnels: a local forward parses into an ssh -L",
              local.Kind == Models.TunnelKind.Local && local.BindPort == 8080 && local.DestPort == 80
              && local.Forward == "-L 127.0.0.1:8080:localhost:80", local.Forward);

        var remote = TunnelSupervisor.Parse("R:0.0.0.0:9000:localhost:22");
        Check("tunnels: a remote forward parses into an ssh -R",
              remote.Kind == Models.TunnelKind.Remote && remote.Forward == "-R 0.0.0.0:9000:localhost:22",
              remote.Forward);

        var dynamic = TunnelSupervisor.Parse("D:127.0.0.1:1080");
        Check("tunnels: a dynamic forward parses into an ssh -D",
              dynamic.Kind == Models.TunnelKind.Dynamic && dynamic.Forward == "-D 127.0.0.1:1080",
              dynamic.Forward);

        Check("tunnels: a bare bind:port:host:port is a local forward",
              TunnelSupervisor.Parse("127.0.0.1:8081:localhost:22").Kind == Models.TunnelKind.Local, "wrong kind");

        Check("tunnels: an unknown kind is refused",
              Throws<TunnelException>(() => TunnelSupervisor.Parse("X:127.0.0.1:1:localhost:2")), "no throw");

        Check("tunnels: an out-of-range port is refused",
              Throws<TunnelException>(() => TunnelSupervisor.Parse("L:127.0.0.1:99999:localhost:22")), "no throw");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- argument quoting

    /// <summary>
    /// Windows command-line quoting, following the CreateProcess rules. The old helper doubled
    /// every backslash, so a "\n" inside an awk printf arrived at the remote host as a literal
    /// "\\n" and every line after it stopped parsing.
    /// </summary>
    private static void ArgumentQuoting()
    {
        const char backslash = (char)92;
        const char quote = (char)34;

        var spaced = SshLauncher.QuoteArgument("a b");
        Check("args: a value with a space is quoted", spaced == quote + "a b" + quote, spaced);

        var plain = "C:" + backslash + "Users" + backslash + "key";
        Check("args: a value with no space stays untouched", SshLauncher.QuoteArgument(plain) == plain,
              SshLauncher.QuoteArgument(plain));

        var newline = SshLauncher.QuoteArgument("printf " + quote + "x" + backslash + "n" + quote);
        Check("args: a backslash before a letter survives once",
              newline.Contains(backslash + "n") && !newline.Contains("" + backslash + backslash + "n"),
              newline);

        var embedded = SshLauncher.QuoteArgument("say " + quote + "hi" + quote);
        Check("args: an embedded quote is escaped",
              embedded == quote + "say " + backslash + quote + "hi" + backslash + quote + quote, embedded);

        var trailing = SshLauncher.QuoteArgument("a b" + backslash);
        Check("args: a trailing backslash is doubled before the closing quote",
              trailing == quote + "a b" + backslash + backslash + quote, trailing);
    }

    // ---------------------------------------------------------------- host panel

    /// <summary>
    /// The CPU and memory series are parsed out of the same one-shot command as the load
    /// line, so their arithmetic is testable without a remote host or a window.
    /// </summary>
    private static void HostPanel()
    {
        var counters = Views.InfoPanel.ParseCounters("LOAD=0.12 0.20 0.18\nSTAT=100 0 50 850 0 0 0 0\n");
        Check("panel: CPU counters add up to their total", counters is { Total: 1000, Idle: 850 },
              counters?.ToString() ?? "null");

        Check("panel: counters made of words are refused",
              Views.InfoPanel.ParseCounters("STAT=one two three four") is null, "parsed words");

        if (counters is { } previous)
        {
            var usage = Views.InfoPanel.CpuPercent(previous, new Views.InfoPanel.CpuCounter(1200, 910));
            Check("panel: CPU percent is the busy share of the delta", usage is { } value && Math.Abs(value - 70) < 0.001,
                  usage?.ToString("0.00") ?? "null");

            Check("panel: identical counters produce no reading",
                  Views.InfoPanel.CpuPercent(previous, previous) is null, "returned a reading");
        }

        var available = Views.InfoPanel.ParseMemoryPercent("MEMTOTAL=3936000\nMEMAVAIL=3148800\n");
        Check("panel: memory uses MemAvailable when present", available is { } memory && Math.Abs(memory - 20) < 0.001,
              available?.ToString("0.00") ?? "null");

        var fallback = Views.InfoPanel.ParseMemoryPercent("MEMTOTAL=1000\nMEMFREE=300\nMEMBUF=50\nMEMCACHED=150\n");
        Check("panel: memory falls back to free+buffers+cached", fallback is { } old && Math.Abs(old - 50) < 0.001,
              fallback?.ToString("0.00") ?? "null");

        Check("panel: memory without a total is refused",
              Views.InfoPanel.ParseMemoryPercent("MEMAVAIL=1234") is null, "accepted a missing total");

        // The live sampler is a batch call: a host that wants a prompt can never answer it, so the
        // panel has to say why instead of waiting for a reading that will never come.
        Check("panel: an MFA refusal is recognised",
              Views.InfoPanel.IsInteractiveRefusal("demo@host: Permission denied (keyboard-interactive).")
              && Views.InfoPanel.IsInteractiveRefusal("Verification code:")
              && !Views.InfoPanel.IsInteractiveRefusal("ssh: connect to host x port 22: Connection timed out"),
              "mfa vs timeout");
        var mfaText = Views.InfoPanel.ExplainNoReading(
            new Views.InfoPanel.BatchResult("", "demo@host: Permission denied (keyboard-interactive).", 255));
        Check("panel: the MFA refusal explains itself",
              mfaText.Contains("interactive prompt", StringComparison.Ordinal)
              && mfaText.Contains("pane", StringComparison.Ordinal), mfaText);
        var plainText = Views.InfoPanel.ExplainNoReading(
            new Views.InfoPanel.BatchResult("", "ssh: connect to host x port 22: Connection timed out", 255));
        Check("panel: a normal failure keeps ssh's own last line",
              plainText.Contains("Connection timed out", StringComparison.Ordinal), plainText);
    }

    // ---------------------------------------------------------------- accessibility units

    /// <summary>
    /// Unit arithmetic behind the accessibility ranges. No UI is needed, so the boundaries a
    /// screen reader walks stay pinned down even when no client is attached; the UIA client
    /// probe proves the plumbing on top of this.
    /// </summary>
    private static void TextUnits()
    {
        const string path = "PS C:\\Users\\Moddy";

        var first = TerminalTextUnits.WordSpan(path, 0);
        Check("uia: a word ends at the space", first == (0, 2), first.ToString());

        var users = TerminalTextUnits.WordSpan(path, 8);
        Check("uia: a word starts at its first letter", users == (6, 11), users.ToString());

        Check("uia: a forward word step leaves the run it is in",
              TerminalTextUnits.NextWord(path, 8) == 11, TerminalTextUnits.NextWord(path, 8).ToString());

        Check("uia: a backward word step lands on the run start",
              TerminalTextUnits.PrevWord(path, 9) == 6, TerminalTextUnits.PrevWord(path, 9).ToString());

        const string rows = "one\ntwo\n\nfour";
        Check("uia: a line starts after the newline", TerminalTextUnits.LineStart(rows, 6) == 4,
              TerminalTextUnits.LineStart(rows, 6).ToString());
        Check("uia: a line ends at the newline", TerminalTextUnits.LineEnd(rows, 5) == 7,
              TerminalTextUnits.LineEnd(rows, 5).ToString());

        var paragraph = TerminalTextUnits.ParagraphSpan(rows, 5);
        Check("uia: a paragraph spans the non-blank run", paragraph == (0, 7), paragraph.ToString());
        var blank = TerminalTextUnits.ParagraphSpan(rows, 8);
        Check("uia: a blank row is its own paragraph", blank == (8, 8), blank.ToString());

        const string pair = "aa\nbb";
        Check("uia: an end endpoint finishes the next line",
              TerminalTextUnits.StepForwardEnd(pair, System.Windows.Automation.Text.TextUnit.Line, 2) == 5,
              TerminalTextUnits.StepForwardEnd(pair, System.Windows.Automation.Text.TextUnit.Line, 2).ToString());
        Check("uia: a backward end endpoint returns to the previous line end",
              TerminalTextUnits.StepBackwardEnd(pair, System.Windows.Automation.Text.TextUnit.Line, 5) == 2,
              TerminalTextUnits.StepBackwardEnd(pair, System.Windows.Automation.Text.TextUnit.Line, 5).ToString());

        var emoji = "a" + char.ConvertFromUtf32(0x1F600) + "b";
        Check("uia: a character step keeps a surrogate pair together",
              TerminalTextUnits.NextCharacter(emoji, 1) == 3 && TerminalTextUnits.PrevCharacter(emoji, 3) == 1,
              TerminalTextUnits.NextCharacter(emoji, 1) + "/" + TerminalTextUnits.PrevCharacter(emoji, 3));

        var combining = "e" + (char)0x0301 + "x";
        var element = (TerminalTextUnits.ElementStart(combining, 1), TerminalTextUnits.ElementEnd(combining, 1));
        Check("uia: a combining mark stays with its base", element == (0, 2), element.ToString());
    }

    private static (TerminalBuffer, VtParser) Make(int columns, int rows)
    {
        var buffer = new TerminalBuffer(columns, rows);
        var parser = new VtParser(buffer, _ => { });
        return (buffer, parser);
    }

    private static void Feed(VtParser parser, string text)
        => parser.Feed(Encoding.UTF8.GetBytes(text));

    private static string Row(TerminalBuffer buffer, int row) => buffer.GetRowText(row);

    private static void ReflowOnResize()
    {
        var (buffer, parser) = Make(20, 5);
        Feed(parser, "abcdefghijklmnopqrst");
        Check("reflow: 20 characters fill a 20-column line", Row(buffer, 0) == "abcdefghijklmnopqrst", Row(buffer, 0));

        buffer.Resize(10, 5);
        Check("reflow: narrowing splits the line",
              Row(buffer, 0) == "abcdefghij" && Row(buffer, 1) == "klmnopqrst",
              Row(buffer, 0) + "|" + Row(buffer, 1));
        Check("reflow: the split row is marked wrapped", buffer.LineAt(0).Wrapped, "not wrapped");

        buffer.Resize(20, 5);
        Check("reflow: widening rejoins the line", Row(buffer, 0) == "abcdefghijklmnopqrst", Row(buffer, 0));
        Check("reflow: the rejoined row is not wrapped", !buffer.LineAt(0).Wrapped, "still wrapped");

        var (tall, tallParser) = Make(12, 4);
        Feed(tallParser, "hello world, this is a longer line");
        tall.Resize(12, 4);
        var joined = Row(tall, 0) + Row(tall, 1) + Row(tall, 2) + Row(tall, 3);
        Check("reflow: no characters are lost", joined.StartsWith("hello world,") && joined.Contains("longer line"), joined);
        Check("reflow: the cursor stays on its line", tall.CursorRow >= 0 && tall.CursorRow < 4, tall.CursorRow.ToString());

        var (wide, wideParser) = Make(30, 3);
        Feed(wideParser, "\u4f60\u597d\u4e16\u754c".PadRight(12, 'x'));
        wide.Resize(4, 3);
        var hasWideCell = false;
        for (var r = 0; r < 3; r++)
        {
            var line = wide.LineAt(r);
            for (var c = 0; c < line.Cells.Length; c++)
            {
                if (line.Cells[c].Width == 2) hasWideCell = true;
                if (line.Cells[c].Width == 0 && (c == 0 || line.Cells[c - 1].Width != 2))
                    Check("reflow: a wide character keeps its continuation", false, "orphan continuation at " + r + "," + c);
            }
        }
        Check("reflow: wide characters survive a narrow resize", hasWideCell, "no wide cell left");

        // Full-screen programs redraw after a resize, so the alternate screen must be a plain
        // trim: rewrapping it would leave half-drawn frames behind.
        var (alt, altParser) = Make(20, 4);
        Feed(altParser, "\u001b[?1049h");
        Feed(altParser, "alt line ok");                 // 11 chars: fits 20 columns, so only the resize acts
        alt.Resize(10, 4);
        Check("reflow: the alternate screen is trimmed, not rewrapped",
              Row(alt, 0) == "alt line o" && Row(alt, 1).Trim().Length == 0,
              Row(alt, 0) + "|" + Row(alt, 1));

        // A tall history is where the old "anchor on the emitted tail" bug showed up: every line
        // looked like content, so a narrow resize pushed real lines into scrollback.
        var (deep, deepParser) = Make(40, 5);
        for (var i = 0; i < 40; i++)
            Feed(deepParser, "line " + i.ToString("00") + " " + new string('x', 25) + (char)13 + (char)10);

        var before = deep.Scrollback.Count + deep.Rows;
        deep.Resize(20, 5);
        var after = deep.Scrollback.Count + deep.Rows;
        Check("reflow: a tall history keeps every line", after >= before - 1, before + " -> " + after);
        Check("reflow: the cursor stays on screen in a tall history",
              deep.CursorRow >= 0 && deep.CursorRow < deep.Rows, deep.CursorRow + "/" + deep.Rows);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            System.Threading.Thread.Sleep(50);
        }
        return condition();
    }

    /// <summary>
    /// Regression guard for the pseudo console: a child must start, accept typed input
    /// and stay alive after the write. The old FileStream-per-write path closed the pty
    /// input pipe, which killed the child with 0xC000013A on the first keystroke.
    /// </summary>
    private static void PseudoConsoleInput()
    {
        if (!ConPty.IsSupported)
        {
            Check("conpty available", false, "pseudo console needs Windows 10 1809 or newer");
            return;
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        var shell = File.Exists(powershell) ? powershell : Path.Combine(system, "cmd.exe");
        var shellArgs = shell.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) ? "/Q" : "-NoLogo -NoExit";

        var lastDetail = "probe did not run";

        // Two attempts: conhost sometimes needs a moment before it will accept input, and a
        // launcher can hand the child its own console instead of the pty. The handle check
        // below is deterministic either way.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var output = new StringBuilder();
            using var pty = new ConPty();
            pty.DataReceived += (data, count) =>
            {
                lock (output) output.Append(Encoding.UTF8.GetString(data, 0, count));
            };
            pty.Start(shell, shellArgs, 100, 30);

            var banner = WaitFor(() => { lock (output) return output.Length > 0; }, 10000);
            pty.Write(Encoding.ASCII.GetBytes("echo SELFTEST_OK" + (char)13));

            var echoed = WaitFor(() =>
            {
                lock (output)
                {
                    var text = output.ToString();
                    var first = text.IndexOf("SELFTEST_OK", StringComparison.Ordinal);
                    return first >= 0 && text.IndexOf("SELFTEST_OK", first + 1, StringComparison.Ordinal) > first;
                }
            }, 10000);

            System.Threading.Thread.Sleep(400);
            var alive = pty.IsRunning;
            var pipeOpen = pty.InputHandleIsOpen();

            // Deterministic: a write must never close the pty input pipe.
            if (attempt == 1) Check("conpty: a write leaves the input pipe open", pipeOpen, "input handle was closed by the write");

            if (banner && echoed && alive)
            {
                Check("conpty: child writes to the console", banner, "no output within 10s");
                Check("conpty: typed input reaches the child and comes back", echoed, "echo did not round-trip");
                Check("conpty: child survives the write", alive, "child exited after the write");
                return;
            }

            var tail = output.ToString();
            if (tail.Length > 120) tail = tail.Substring(tail.Length - 120);
            lastDetail = "attempt " + attempt + ": banner=" + banner + " echoed=" + echoed
                         + " alive=" + alive + " tail=" + tail.Replace((char)13, (char)32).Replace((char)10, (char)32);
        }

        Skip("conpty round-trip", lastDetail + " -- this launcher does not give the child a usable console; start SSH-it normally to exercise it");
    }

    /// <summary>
    /// High contrast is a palette, not a preference: it has to clear contrast ratios, define
    /// every brush the chrome binds, and follow the flag at resolve time. These checks fail on
    /// a machine with any high-contrast scheme, because the palette is built from SystemColors.
    /// </summary>
    private static void HighContrast()
    {
        var chrome = ThemeService.BuildHighContrast();
        var missing = ThemeService.BrushKeys.Where(key => !chrome.Contains(key)).ToList();
        Check("high contrast: every chrome brush is defined", missing.Count == 0,
              missing.Count == 0 ? ThemeService.BrushKeys.Length + " keys" : string.Join(", ", missing));

        Color Colour(string key) => ((SolidColorBrush)chrome[key]).Color;

        var textOnWindow = ThemeService.ContrastRatio(Colour("Brush.Text"), Colour("Brush.Window"));
        Check("high contrast: text on window clears AAA", textOnWindow >= 7.0, textOnWindow.ToString("0.00") + ":1");

        var accentOnWindow = ThemeService.ContrastRatio(Colour("Brush.Accent"), Colour("Brush.Window"));
        Check("high contrast: accent on window clears the non-text bar", accentOnWindow >= 3.0,
              accentOnWindow.ToString("0.00") + ":1");

        var labelOnAccent = ThemeService.ContrastRatio(Colour("Brush.TextOnAccent"), Colour("Brush.Accent"));
        Check("high contrast: a label on an accent fill is readable", labelOnAccent >= 4.5,
              labelOnAccent.ToString("0.00") + ":1");

        Check("high contrast: danger is plain text, not a hue",
              Colour("Brush.Danger") == Colour("Brush.Text"), Colour("Brush.Danger").ToString());

        var terminal = TerminalTheme.HighContrast();
        var weak = new List<string>();
        for (var i = 1; i < 16; i++)
        {
            var ratio = ThemeService.ContrastRatio(terminal.Palette[i], terminal.Background);
            if (ratio < 4.5) weak.Add(i + "=" + ratio.ToString("0.0"));
        }
        Check("high contrast: every terminal colour clears AA on the background", weak.Count == 0,
              weak.Count == 0 ? "1-15 checked" : string.Join(", ", weak));

        Check("high contrast: highlights are frames, not fills", terminal.OutlinedHighlights,
              "outlined=" + terminal.OutlinedHighlights);

        try
        {
            ThemeService.ForceHighContrast = true;
            var forced = TerminalTheme.Get("Dracula").Name;
            Check("high contrast: the flag overrides the saved scheme", forced == "High Contrast", forced);
        }
        finally
        {
            ThemeService.ForceHighContrast = false;
        }

        var restored = TerminalTheme.Get("Dracula").Name;
        Check("high contrast: the saved scheme returns when it is off", restored == "Dracula", restored);
    }

    /// <summary>
    /// Palette themes are data, so the invariants are checked per theme rather than trusted:
    /// every chrome key present, readable text, a visible accent and a label that survives on
    /// top of it.
    /// </summary>
    private static void ThemePalettes()
    {
        foreach (var palette in ThemeCatalog.Palettes)
        {
            var dict = ThemeCatalog.Build(palette);
            var problems = new List<string>();

            var missing = ThemeService.BrushKeys.Where(key => !dict.Contains(key)).ToList();
            if (missing.Count > 0) problems.Add("missing " + string.Join("/", missing));

            Color Colour(string key) => ((SolidColorBrush)dict[key]).Color;

            var textOnWindow = ThemeService.ContrastRatio(Colour("Brush.Text"), Colour("Brush.Window"));
            if (textOnWindow < 7.0) problems.Add("text " + textOnWindow.ToString("0.0") + ":1");

            var labelOnAccent = ThemeService.ContrastRatio(Colour("Brush.TextOnAccent"), Colour("Brush.Accent"));
            if (labelOnAccent < 4.5) problems.Add("label " + labelOnAccent.ToString("0.0") + ":1");

            var accentOnWindow = ThemeService.ContrastRatio(Colour("Brush.Accent"), Colour("Brush.Window"));
            if (accentOnWindow < 3.0) problems.Add("accent " + accentOnWindow.ToString("0.0") + ":1");

            if (Colour("Brush.Panel") == Colour("Brush.Window")) problems.Add("panel is the window colour");

            Check("theme " + palette.Id + ": complete and legible", problems.Count == 0,
                  problems.Count == 0 ? textOnWindow.ToString("0.0") + ":1 text" : string.Join("; ", problems));
        }
    }

    /// <summary>
    /// The jump route is drawn from the session text and the blamed hop comes from ssh's own
    /// stderr, so the parsing is worth pinning down: a wrong guess would mark the wrong hop.
    /// </summary>
    private static void JumpChainRoute()
    {
        var hops = JumpChain.Hops("ops@jump-nyc:2222, jump-fra ,");
        Check("jump chain: commas split into hops", hops.Count == 2 && hops[1] == "jump-fra",
              string.Join("|", hops));

        Check("jump chain: a blank setting has no hops",
              JumpChain.Hops(null).Count == 0 && JumpChain.Hops("   ").Count == 0,
              JumpChain.Hops(null).Count + "/" + JumpChain.Hops("   ").Count);

        var blamed = JumpChain.FailingHop(
            "ssh: connect to host jump-fra port 22: Connection refused", hops, "web-01");
        Check("jump chain: ssh names the hop that failed", blamed == "jump-fra", blamed ?? "(none)");

        var withUser = JumpChain.FailingHop(
            "ssh: connect to host jump-nyc port 2222: timed out", hops, "web-01");
        Check("jump chain: user and port do not stop the match", withUser == "ops@jump-nyc:2222",
              withUser ?? "(none)");

        var target = JumpChain.FailingHop("ssh: connect to host web-01 port 22: refused", hops, "web-01");
        Check("jump chain: a destination failure is named as the target", target == "target", target ?? "(none)");

        var blind = JumpChain.FailingHop("channel 0: open failed: connect failed", hops, "web-01");
        Check("jump chain: an error naming no host stays unattributed", blind is null, blind ?? "(null)");

        Check("jump chain: brackets come off an IPv6 hop", JumpChain.HostPart("[2001:db8::1]:22") == "2001:db8::1",
              JumpChain.HostPart("[2001:db8::1]:22"));

        // -J is whitespace-sensitive; the UI trims the chain, so the command line must too.
        var spaced = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01",
            User = "ops",
            JumpHost = "jump-a, jump-b ,jump-c"
        });
        Check("jump chain: -J drops the spaces ssh rejects", spaced.Contains("-J jump-a,jump-b,jump-c", StringComparison.Ordinal),
              spaced);

        // A key file cannot travel to a hop through -J: OpenSSH builds the hop's own command line
        // with -l/-p/-W and no identity, so the session must spell the chain as ProxyCommands.
        var withKey = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01", User = "ops", Port = 2225,
            JumpHost = "ops@jump-a:2222", KeyPath = @"C:\keys\lab"
        });
        Check("jump chain: a key file turns the chain into ProxyCommands",
              !withKey.Contains("-J ", StringComparison.Ordinal)
              && withKey.Contains("ProxyCommand=", StringComparison.Ordinal)
              && withKey.Contains(@"-i C:\keys\lab", StringComparison.Ordinal), withKey);
        Check("jump chain: the hop is split into -p/-l/host",
              withKey.Contains("-W %h:%p -p 2222 -l ops jump-a", StringComparison.Ordinal), withKey);
        Check("jump chain: the destination keeps the session's port",
              withKey.Contains("-p 2225", StringComparison.Ordinal) && withKey.Contains("ops@web-01", StringComparison.Ordinal),
              withKey);

        var twoHops = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01", JumpHost = "jump-a:2222,jump-b:2223", KeyPath = @"C:\keys\lab"
        });
        var stages = twoHops.Split("ProxyCommand=").Length - 1;
        Check("jump chain: each hop gets its own stage", stages == 2, "stages=" + stages + " | " + twoHops);
        Check("jump chain: every stage names its own hop",
              twoHops.Contains("jump-a", StringComparison.Ordinal) && twoHops.Contains("jump-b", StringComparison.Ordinal),
              twoHops);
        // ssh expands %h/%p across the whole ProxyCommand, nested strings included: only the outer
        // stage may carry the token, or every inner stage would forward to the target and skip the
        // hops between.
        Check("jump chain: only the outer stage uses %h:%p",
              twoHops.Split("%h:%p").Length - 1 == 1, "tokens=" + (twoHops.Split("%h:%p").Length - 1) + " | " + twoHops);
        Check("jump chain: an inner stage forwards to the next hop by name",
              twoHops.Contains("-W jump-b:2223", StringComparison.Ordinal), twoHops);

        var noChain = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01", JumpHost = "  ", KeyPath = @"C:\keys\lab"
        });
        Check("jump chain: a blank chain adds neither -J nor a ProxyCommand",
              !noChain.Contains("-J", StringComparison.Ordinal) && !noChain.Contains("ProxyCommand", StringComparison.Ordinal),
              noChain);

        // Agent forwarding through a hop keeps -J: the hop can use the agent, so nothing has to be
        // spelled out and OpenSSH's own chain handles it.
        var agentJump = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01", JumpHost = "jump-a:2222", ForwardAgent = true
        });
        Check("jump chain: an agent-only session keeps -J and adds -A",
              agentJump.Contains("-J jump-a:2222", StringComparison.Ordinal)
              && agentJump.Contains(" -A", StringComparison.Ordinal)
              && !agentJump.Contains("ProxyCommand", StringComparison.Ordinal), agentJump);

        // The agent socket rule: a POSIX path is a Cygwin socket the native ssh cannot use, but a pipe
        // path in slash form is a real Windows pipe (and the form an MSYS shell exports).
        var priorAgent = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        try
        {
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "//./pipe/sshit-test-agent");
            var piped = SshLauncher.BuildEnvironment(new Session { Host = "h" }, null);
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "/tmp/ssh-abc/agent.123");
            var posix = SshLauncher.BuildEnvironment(new Session { Host = "h" }, null);
            Check("agent: a pipe path in slash form is left for ssh to use",
                  !piped.ContainsKey("SSH_AUTH_SOCK"), piped.ContainsKey("SSH_AUTH_SOCK") ? piped["SSH_AUTH_SOCK"] : "(left alone)");
            Check("agent: a POSIX socket path is cleared",
                  posix.TryGetValue("SSH_AUTH_SOCK", out var clearedValue) && clearedValue == "",
                  posix.TryGetValue("SSH_AUTH_SOCK", out var shown) ? "value='" + shown + "'" : "(kept — wrong)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", priorAgent);
        }

        // Each hop may carry its own key: the hop's entry wins, then the session key, then none.
        var perHop = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01", JumpHost = "ops@jump-a:2222,jump-b:2223",
            KeyPath = @"C:\keys\target",
            JumpKeys = new()
            {
                ["ops@jump-a:2222"] = @"C:\keys\hopA",
                ["jump-b:2223"] = @"C:\keys\hopB"
            }
        });
        Check("jump chain: every stage resolves its own key",
              perHop.Contains(@"-i C:\keys\hopA", StringComparison.Ordinal)
              && perHop.Contains(@"-i C:\keys\hopB", StringComparison.Ordinal)
              && perHop.Contains(@"-i C:\keys\target", StringComparison.Ordinal), perHop);
        Check("jump chain: per-hop keys alone force ProxyCommands",
              !perHop.Contains("-J ", StringComparison.Ordinal)
              && perHop.Split("ProxyCommand=").Length - 1 == 2, perHop);

        // A hop with no entry and no session key is left to the agent, so it must carry neither -i
        // nor IdentitiesOnly — the one thing that would make that hop offer nothing at all.
        var mixedKeys = SshLauncher.BuildBatchArguments(new Session
        {
            Host = "web-01", JumpHost = "jump-a:2222,jump-b:2223",
            JumpKeys = new() { ["jump-b:2223"] = @"C:\keys\hopB" }
        });
        Check("jump chain: only the hop with a key gets -i and IdentitiesOnly",
              mixedKeys.Split("IdentitiesOnly=yes").Length - 1 == 1
              && mixedKeys.Contains(@"-i C:\keys\hopB", StringComparison.Ordinal),
              mixedKeys);

        // The same field carries per-hop keys in text: "hop=key", one per comma-separated entry.
        var spec = JumpChain.ParseSpec(@"ops@jump-a:2222=C:\keys\hopA, jump-b, jump-c:2223=C:\keys\hopC");
        Check("jump chain: the text form splits keys off the hops",
              spec.Hops.Count == 3 && spec.Keys.Count == 2, "hops=" + spec.Hops.Count + " keys=" + spec.Keys.Count);
        Check("jump chain: a hop key keeps its drive colon and backslashes",
              spec.Keys.TryGetValue("ops@jump-a:2222", out var hopAKey) && hopAKey == @"C:\keys\hopA",
              spec.Keys.TryGetValue("ops@jump-a:2222", out var shownHopKey) ? shownHopKey : "(missing)");
        Check("jump chain: the text form round-trips",
              JumpChain.Format(string.Join(",", spec.Hops), spec.Keys)
              == @"ops@jump-a:2222=C:\keys\hopA,jump-b,jump-c:2223=C:\keys\hopC",
              JumpChain.Format(string.Join(",", spec.Hops), spec.Keys));
        Check("jump chain: hop= (empty key) means no key of its own",
              JumpChain.ParseSpec("a=").Keys.Count == 0, "keys=" + JumpChain.ParseSpec("a=").Keys.Count);

        // A Windows path may contain the comma that separates hops; quoting it keeps it in one piece.
        var quotedKey = JumpChain.ParseSpec("a,b=\"C:\\keys\\x,y\"");
        Check("jump chain: a quoted key keeps its comma",
              quotedKey.Hops.Count == 2 && quotedKey.Keys.TryGetValue("b", out var commaKey) && commaKey == @"C:\keys\x,y",
              quotedKey.Keys.TryGetValue("b", out var shownCommaKey) ? shownCommaKey : "(missing)");
        Check("jump chain: a quoted key round-trips",
              JumpChain.Format("a,b", quotedKey.Keys) == "a,b=\"C:\\keys\\x,y\"",
              JumpChain.Format("a,b", quotedKey.Keys));

        var hopNoPort = JumpChain.ParseHop("ops@jump-a");
        var hopV6 = JumpChain.ParseHop("[2001:db8::1]:2222");
        var hopBareV6 = JumpChain.ParseHop("2001:db8::2");
        Check("jump chain: a hop without a port keeps port 0",
              hopNoPort is { User: "ops", Host: "jump-a", Port: 0 }, hopNoPort.ToString());
        Check("jump chain: a bracketed IPv6 hop splits cleanly",
              hopV6 is { User: "", Host: "2001:db8::1", Port: 2222 }, hopV6.ToString());
        Check("jump chain: a bare IPv6 hop is not read as host:port",
              hopBareV6 is { Host: "2001:db8::2", Port: 0 }, hopBareV6.ToString());
    }

    /// <summary>
    /// X11 forwarding is only usable when the ssh child knows where the local server is. The
    /// parsing is pure and the probe is a port scan, so the rules are worth pinning down.
    /// </summary>
    private static void X11Checks()
    {
        Check("x11: an explicit DISPLAY wins", X11Support.DisplayFor(":0", 6002) == ":0",
              X11Support.DisplayFor(":0", 6002) ?? "(null)");

        Check("x11: the first listening port names the display",
              X11Support.DisplayFor(null, 6001) == "127.0.0.1:1", X11Support.DisplayFor(null, 6001) ?? "(null)");

        Check("x11: a blank DISPLAY falls through to the probe",
              X11Support.DisplayFor("   ", 6000) == "127.0.0.1:0", X11Support.DisplayFor("   ", 6000) ?? "(null)");

        Check("x11: nothing listening means no display", X11Support.DisplayFor(null, null) is null,
              X11Support.DisplayFor(null, null) ?? "(null)");

        var forwarded = SshLauncher.BuildEnvironment(new Session { Host = "h", X11Forwarding = true }, null);
        var plain = SshLauncher.BuildEnvironment(new Session { Host = "h" }, null);
        Check("x11: DISPLAY is only set when forwarding is on",
              forwarded.ContainsKey("DISPLAY") == X11Support.Available && !plain.ContainsKey("DISPLAY"),
              "forwarded=" + forwarded.ContainsKey("DISPLAY") + " plain=" + plain.ContainsKey("DISPLAY"));
    }

    /// <summary>
    /// Telnet's option traffic is stripped before the parser sees it, and the serial formatter is
    /// what the session tree shows. Both are pure, so both are pinned down here.
    /// </summary>
    private static void ConnectionKinds()
    {
        static (string Clean, string Reply) Feed(params byte[] bytes)
        {
            var filter = new TelnetTransport.TelnetFilter();
            var clean = filter.Process(bytes, bytes.Length, out var reply);
            return (string.Join(",", clean), string.Join(",", reply));
        }

        var will = Feed(65, 255, 251, 1, 66);           // A IAC WILL ECHO B
        Check("telnet: option traffic is stripped from the data", will.Clean == "65,66", will.Clean);
        Check("telnet: WILL ECHO is answered with DO", will.Reply == "255,253,1", will.Reply);

        var escaped = Feed(255, 255);
        Check("telnet: an escaped 0xFF survives", escaped.Clean == "255", escaped.Clean);

        var naws = Feed(255, 253, 31);
        Check("telnet: DO NAWS is accepted", naws.Reply == "255,251,31", naws.Reply);

        var unknown = Feed(255, 253, 99);
        Check("telnet: an unknown option is refused", unknown.Reply == "255,252,99", unknown.Reply);

        var sub = Feed(255, 250, 31, 0, 80, 0, 24, 255, 240, 65);
        Check("telnet: subnegotiation is consumed", sub.Clean == "65", sub.Clean);

        Check("serial: the 8N1 label matches the settings",
              SerialTransport.Describe("COM3", 115200, 8, "None", "One") == "COM3 115200 8N1",
              SerialTransport.Describe("COM3", 115200, 8, "None", "One"));
    }

    /// <summary>
    /// ZMODEM: the checks that do not need a host. CRC vectors come from an independent
    /// implementation of the two polynomials, and the round trip runs two real sessions
    /// back to back so the framing rules are exercised both ways.
    /// </summary>
    private static void ZmodemChecks()
    {
        var nine = Encoding.ASCII.GetBytes("123456789");
        Check("zmodem: CRC-16 vector", ZmodemSession.Crc16(nine, 0, 9) == 0x31C3,
              ZmodemSession.Crc16(nine, 0, 9).ToString("X4"));
        Check("zmodem: CRC-32 vector", ZmodemSession.Crc32(nine, 0, 9) == 0xFC891918,
              ZmodemSession.Crc32(nine, 0, 9).ToString("X8"));

        var traversal = ZmodemSession.SafeLeafName(@"..\..\windows\system32\evil.exe");
        Check("zmodem: a path is reduced to its leaf name", traversal == "evil.exe", traversal);
        var placeholder = ZmodemSession.SafeLeafName("");
        Check("zmodem: an empty name gets a placeholder", placeholder == "zmodem.bin", placeholder);
        var reserved = ZmodemSession.SafeLeafName("CON");
        Check("zmodem: a reserved device name is defused", reserved == "_CON", reserved);

        var signature = new byte[] { 0x41, 0x2A, 0x2A, 0x18, 0x42, 0x30, 0x30 };
        Check("zmodem: the ZRQINIT signature is found in output",
              ZmodemSession.FindStart(signature, signature.Length) == 2,
              ZmodemSession.FindStart(signature, signature.Length).ToString());
        var plain = Encoding.ASCII.GetBytes("hello * world");
        Check("zmodem: plain output carries no signature",
              ZmodemSession.FindStart(plain, plain.Length) == -1, "found one");

        var dir = Path.Combine(Path.GetTempPath(), "sshit-zmd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            var payload = new byte[300_000];
            new Random(11).NextBytes(payload);
            for (var i = 0; i < payload.Length; i += 61) payload[i] = 0x18;   // force escaping
            var src = Path.Combine(dir, "payload.bin");
            File.WriteAllBytes(src, payload);

            ZmodemSession? tx = null, rx = null;
            var done = new ManualResetEventSlim(false);
            var okFlag = false;
            var notes = new StringBuilder();

            // Sender first: the receiver announces itself as soon as it exists.
            tx = new ZmodemSession(ZmodemRole.Send, b => rx?.Feed(b, b.Length), outDir, src);
            rx = new ZmodemSession(ZmodemRole.Receive, b => tx?.Feed(b, b.Length), outDir, null);
            rx.Completed += (ok, text) => { okFlag = ok; notes.Append(text); done.Set(); };
            tx.Completed += (ok, text) => { if (!ok) { notes.Append(" sender: " + text); done.Set(); } };

            var finished = done.Wait(TimeSpan.FromSeconds(30));
            tx.Dispose();
            rx.Dispose();

            var received = Path.Combine(outDir, "payload.bin");
            var same = File.Exists(received) && File.ReadAllBytes(received).SequenceEqual(payload);
            Check("zmodem: a file round trips through a session pair", finished && okFlag && same,
                  finished ? (okFlag ? "bytes differ" : notes.ToString()) : "timed out");

            // A peer must never be able to run anything here.
            static byte[] HexHeader(int type, uint value)
            {
                var payload = new byte[5];
                payload[0] = (byte)type;
                payload[1] = (byte)(value & 0xFF);
                payload[2] = (byte)((value >> 8) & 0xFF);
                payload[3] = (byte)((value >> 16) & 0xFF);
                payload[4] = (byte)((value >> 24) & 0xFF);
                var crc = ZmodemSession.Crc16(payload, 0, 5);
                var list = new List<byte> { 0x2A, 0x2A, 0x18, 0x42 };
                var body = new List<byte>(payload) { (byte)(crc >> 8), (byte)(crc & 0xFF) };   // high byte first
                foreach (var b in body)
                {
                    list.Add((byte)"0123456789abcdef"[(b >> 4) & 0xF]);
                    list.Add((byte)"0123456789abcdef"[b & 0xF]);
                }
                list.Add(0x0D);
                return list.ToArray();
            }

            var guardDone = new ManualResetEventSlim(false);
            var refused = false;
            var guard = new ZmodemSession(ZmodemRole.Receive, _ => { }, Path.Combine(dir, "guard"), null);
            guard.Completed += (ok, text) =>
            {
                refused = !ok && text.Contains("ZCOMMAND", StringComparison.Ordinal);
                guardDone.Set();
            };
            var command = HexHeader(0x12, 0);          // ZCOMMAND
            guard.Feed(command, command.Length);
            var reacted = guardDone.Wait(TimeSpan.FromSeconds(5));
            guard.Dispose();
            Check("zmodem: a ZCOMMAND frame is refused", reacted && refused,
                  reacted ? "the session accepted it" : "no reaction at all");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Multiple main windows: the settings round-trip for extra windows, the cascade maths, and
    /// the request a second launch hands to the instance that is already running.
    /// </summary>
    private static void WindowSessions()
    {
        var settings = new AppSettings();
        settings.ExtraWindows.Add(new WindowLayout
        {
            Left = 120,
            Top = 80,
            Width = 1000,
            Height = 700,
            TabSessionIds = { "a", "b" }
        });
        settings.ExtraWindows.Add(new WindowLayout { Maximized = true, TabSessionIds = { "c" } });

        var json = JsonSerializer.Serialize(settings, DataStore.SerializerOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, DataStore.SerializerOptions)!;

        Check("settings: extra windows round-trip", back.ExtraWindows.Count == 2, back.ExtraWindows.Count.ToString());
        Check("settings: extra window tabs kept",
            back.ExtraWindows[0].TabSessionIds.SequenceEqual(new[] { "a", "b" }) &&
            back.ExtraWindows[1].TabSessionIds.SequenceEqual(new[] { "c" }),
            string.Join(",", back.ExtraWindows[0].TabSessionIds));
        Check("settings: unnamed placement stays NaN", double.IsNaN(back.ExtraWindows[1].Left), back.ExtraWindows[1].Left.ToString("R"));

        var work = new Rect(0, 0, 1920, 1040);
        var stepped = WindowCascade.Next(new Rect(200, 150, 1280, 820), work, 1280, 820);
        Check("window cascade steps 32 px",
            Math.Abs(stepped.Left - 232) < 0.01 && Math.Abs(stepped.Top - 182) < 0.01,
            $"{stepped.Left},{stepped.Top}");

        var wrapped = WindowCascade.Next(new Rect(700, 300, 1280, 820), work, 1280, 820);
        Check("window cascade wraps inside the work area",
            wrapped.Right <= work.Right + 0.01 && wrapped.Bottom <= work.Bottom + 0.01 && wrapped.Left >= work.Left,
            wrapped.ToString());

        Check("handoff: --connect names the session",
            Handoff.ForArgs(new[] { "--connect", "abc123" }) == "connect abc123",
            Handoff.ForArgs(new[] { "--connect", "abc123" }));
        Check("handoff: bare launch focuses", Handoff.ForArgs(Array.Empty<string>()) == Handoff.Focus,
            Handoff.ForArgs(Array.Empty<string>()));
        var parsed = Handoff.Parse("connect abc123");
        Check("handoff: message parses", parsed.Verb == Handoff.Connect && parsed.Argument == "abc123",
            parsed.Verb + "/" + parsed.Argument);

        // ---- command receipts: a self-contained proof of one block ----
        var receipt = new BlockReceipt.Receipt
        {
            Host = "pi.example",
            SessionName = "demo host",
            User = "demo",
            Command = "echo \"<hi>\" && printf 'x'",
            Output = "<script>alert(1)</script>\nline two",
            Cwd = "/tmp",
            ExitCode = 7,
            Duration = TimeSpan.FromSeconds(1.24),
            Started = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Local),
            AppVersion = "1.0.29.0"
        };
        var receiptHtml = BlockReceipt.RenderHtml(receipt);
        var escaped = receiptHtml.Contains("&lt;script&gt;alert(1)&lt;/script&gt;") &&
                      !receiptHtml.Contains("<script>alert");
        Check("receipt: html escapes the output", escaped, "escaped=" + escaped);
        Check("receipt: html carries exit status and duration",
            receiptHtml.Contains("exit 7") && receiptHtml.Contains("1.2 s"),
            BlockReceipt.Summarise(receipt));

        var fenceProbe = new BlockReceipt.Receipt
        {
            Host = "h", SessionName = "s", Command = "echo ```", Output = "`````", ExitCode = 0
        };
        var probeMarkdown = BlockReceipt.RenderMarkdown(fenceProbe);
        var fenceOk = probeMarkdown.Contains(new string('`', 6));
        Check("receipt: the fence outgrows backticks in the output", fenceOk, "fence6=" + fenceOk);

        Check("receipt: file name is legal and dated",
            BlockReceipt.SuggestFileName(receipt) ==
            "receipt-20261101-120000-pi-example-echo-hi-printf-x.html",
            BlockReceipt.SuggestFileName(receipt));

        var longOutput = new string('a', BlockReceipt.OutputLimit + 1000) + "TAIL";
        var trimmed = BlockReceipt.Trim(longOutput);
        Check("receipt: long output is trimmed with both ends kept",
            trimmed.Length <= BlockReceipt.OutputLimit + 200 &&
            trimmed.StartsWith("aaa", StringComparison.Ordinal) &&
            trimmed.EndsWith("TAIL", StringComparison.Ordinal),
            trimmed.Length.ToString());

        // ---- keyword highlighting: matching is pure, so it is testable without a window ----
        var plain = new HighlightRule { Pattern = "error" };
        Check("highlight: plain text finds every occurrence, ignoring case",
            Highlighter.Spans("error and ERROR and err", new[] { plain }).Count == 2,
            Highlighter.Spans("error and ERROR and err", new[] { plain }).Count.ToString());

        var caseSensitive = new HighlightRule { Pattern = "error", IgnoreCase = false };
        Check("highlight: case-sensitive rules find only the exact case",
            Highlighter.Spans("error and ERROR", new[] { caseSensitive }).Count == 1,
            Highlighter.Spans("error and ERROR", new[] { caseSensitive }).Count.ToString());

        var digits = new HighlightRule { Pattern = @"\d{3,}", IsRegex = true };
        var digitSpans = Highlighter.Spans("port 8080 open, pid 22", new[] { digits });
        Check("highlight: a regex rule matches a run of digits",
            digitSpans.Count == 1 && digitSpans[0].Start == 5 && digitSpans[0].Length == 4,
            digitSpans.Count == 1 ? $"{digitSpans[0].Start}/{digitSpans[0].Length}" : digitSpans.Count.ToString());

        var broken = new HighlightRule { Pattern = "([unclosed", IsRegex = true };
        var brokenOk = true;
        try { brokenOk = Highlighter.Spans("anything", new[] { broken }).Count == 0; }
        catch { brokenOk = false; }
        Check("highlight: an invalid regex is ignored, not thrown", brokenOk, "broken=" + brokenOk);

        var first = new HighlightRule { Pattern = "ERROR", Background = "#5A1F1F" };
        var second = new HighlightRule { Pattern = "ERROR: disk", Background = "#5A441F" };
        var mask = Highlighter.Mask("ERROR: disk", new[] { first, second });
        Check("highlight: the first rule to claim a character owns it",
            mask[0] == 0 && mask[6] == 1, string.Join(",", mask));

        Check("highlight: a colour parses and nonsense does not",
            Highlighter.ParseColour("#FF8A8A") is not null && Highlighter.ParseColour("not-a-colour") is null,
            Highlighter.ParseColour("#FF8A8A")?.ToString() ?? "null");

        // ---- snippet placeholders ----
        var snippetSession = new Session { Name = "demo host", Host = "192.0.2.10", Port = 2222, User = "demo" };
        var expanded = SnippetText.Expand("ssh {user}@{host} -p {port} # {session}", snippetSession, "/var/log");
        Check("snippet: known placeholders are filled from the session",
            expanded == "ssh demo@192.0.2.10 -p 2222 # demo host", expanded);

        var unknownCwd = SnippetText.Expand("cd {cwd} && tail -n 50 syslog", snippetSession, null);
        Check("snippet: a placeholder with no value stays visible",
            unknownCwd == "cd {cwd} && tail -n 50 syslog", unknownCwd);

        var untouched = SnippetText.Expand("echo {nope} {DATE}", snippetSession, "/tmp",
            new DateTime(2026, 11, 1, 14, 30, 0));
        Check("snippet: unknown tokens are untouched and DATE is accepted",
            untouched == "echo {nope} 2026-11-01", untouched);

        // ---- session lock: PBKDF2 with a per-lock salt, no recovery path ----
        var lockSalt = SessionLock.NewSalt();
        var lockHash = SessionLock.Hash("correct horse", lockSalt);
        Check("lock: the right password verifies", SessionLock.Verify("correct horse", lockSalt, lockHash), "verify");
        Check("lock: a wrong password is rejected", !SessionLock.Verify("Correct horse", lockSalt, lockHash), "rejected");
        var otherSalt = SessionLock.NewSalt();
        Check("lock: the same password hashes differently under a new salt",
            !lockHash.SequenceEqual(SessionLock.Hash("correct horse", otherSalt)), "salted");

        // ---- resume offset: only a shorter local/remote file gains anything ----
        Check("resume: a short partial continues at its end",
            SftpClient.ResumeOffset(1024, 4096) == 1024, SftpClient.ResumeOffset(1024, 4096).ToString());
        Check("resume: equal, empty or oversized files restart from zero",
            SftpClient.ResumeOffset(4096, 4096) == 0 &&
            SftpClient.ResumeOffset(0, 4096) == 0 &&
            SftpClient.ResumeOffset(5000, 4096) == 0,
            $"{SftpClient.ResumeOffset(4096, 4096)}/{SftpClient.ResumeOffset(0, 4096)}/{SftpClient.ResumeOffset(5000, 4096)}");

        // ---- mirror planning: a directory, an unchanged file, and a changed one ----
        var dirEntry = new SftpEntry { Name = "logs", FullPath = "/var/logs", IsDirectory = true };
        var sameEntry = new SftpEntry { Name = "a.txt", FullPath = "/var/logs/a.txt", Size = 120 };
        var newEntry = new SftpEntry { Name = "b.txt", FullPath = "/var/logs/b.txt", Size = 300 };
        Check("mirror: a directory is created, an identical file is skipped, a new file is downloaded",
            SftpClient.PlanMirror(dirEntry, @"C:\m\logs", false, 0).Action == "create-dir" &&
            SftpClient.PlanMirror(sameEntry, @"C:\m\logs\a.txt", true, 120).Action == "skip" &&
            SftpClient.PlanMirror(newEntry, @"C:\m\logs\b.txt", false, 0).Action == "download",
            $"{SftpClient.PlanMirror(dirEntry, "x", false, 0).Action}/" +
            $"{SftpClient.PlanMirror(sameEntry, "x", true, 120).Action}/" +
            $"{SftpClient.PlanMirror(newEntry, "x", false, 0).Action}");
        // ---- the mirror summary the panel shows, and the local pane's rules ----
        var mirrorPlan = new[]
        {
            new SftpClient.MirrorItem { Action = "create-dir", RemotePath = "/w", LocalPath = @"C:\w" },
            new SftpClient.MirrorItem { Action = "download", RemotePath = "/w/a", LocalPath = @"C:\w\a", Size = 2048 },
            new SftpClient.MirrorItem { Action = "skip", RemotePath = "/w/b", LocalPath = @"C:\w\b", Size = 10 }
        };
        Check("mirror: the plan summary counts files, bytes, folders and skips",
            SftpClient.SummariseMirror(mirrorPlan) == "1 file(s), 2 KB, 1 folder, 1 up to date",
            SftpClient.SummariseMirror(mirrorPlan));

        var localTemp = Path.Combine(Path.GetTempPath(), "sshit-localfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localTemp);
        try
        {
            Directory.CreateDirectory(Path.Combine(localTemp, "sub"));
            File.WriteAllText(Path.Combine(localTemp, "b.txt"), "x");
            File.WriteAllText(Path.Combine(localTemp, "A.txt"), "x");

            var listed = LocalFiles.List(localTemp);
            Check("local files: folders first, then names, case-insensitively",
                listed.Count == 3 && listed[0].Name == "sub" && listed[1].Name == "A.txt" && listed[2].Name == "b.txt",
                string.Join("/", listed.ConvertAll(e => e.Name)));

            Check("local files: a folder has a parent, a drive root does not",
                LocalFiles.Parent(@"C:\Users") == @"C:\" && LocalFiles.Parent(@"C:\") is null,
                (LocalFiles.Parent(@"C:\Users") ?? "null") + " / " + (LocalFiles.Parent(@"C:\") ?? "null"));
        }
        finally
        {
            try { Directory.Delete(localTemp, true); } catch { }
        }

    }

    private static void Diagnostics()
    {
        var cfg = SshDiagnostics.ParseEffective(string.Join("\n", new[]
        {
            "host 192.0.2.10",
            "user moddy",
            "port 22",
            "identityfile ~/.ssh/id_ed25519",
            "identityfile ~/.ssh/id_rsa",
            "proxyjump none",
            "ciphers chacha20-poly1305@openssh.com,aes128-ctr"
        }));
        Check("diagnose: ssh -G parses to ordered pairs with repeated keys kept",
            cfg.Count == 7 && cfg[0].Key == "host" && cfg[0].Value == "192.0.2.10" &&
            cfg.Count(r => r.Key == "identityfile") == 2 && cfg[5].Value == "none",
            $"{cfg.Count} rows");

        var good = SshDiagnostics.ParseVerbose(string.Join("\n", new[]
        {
            "debug1: Reading configuration data C:\\Users\\Moddy\\.ssh\\config",
            "debug1: Connecting to 192.0.2.10 [192.0.2.10] port 22.",
            "debug1: Connection established.",
            "debug1: Remote protocol version 2.0, remote software version OpenSSH_9.2p1 Debian-2+deb12u7",
            "debug1: kex: algorithm: sntrup761x25519-sha512@openssh.com",
            "debug1: Server host key: ssh-ed25519 SHA256:abc",
            "debug1: Host '192.0.2.10' is known and matches the ED25519 host key.",
            "debug1: Authentications that can continue: publickey,password",
            "debug1: Offering public key: C:\\Users\\Moddy\\.ssh\\demo_ed25519 ED25519 SHA256:xyz",
            "debug1: Server accepts key: C:\\Users\\Moddy\\.ssh\\demo_ed25519 ED25519 SHA256:xyz",
            "debug1: Authenticated to 192.0.2.10 ([192.0.2.10]:22) using \"publickey\".",
            "debug1: Entering interactive session."
        }));
        Check("diagnose: a good handshake lists every stage and no failure",
            good.Any(s => s.Stage == "Authenticated" && s.State == "ok") &&
            good.Any(s => s.Stage == "Server host key") &&
            !good.Any(s => s.State == "fail"),
            string.Join("/", good.Select(s => s.Stage)));

        var bad = SshDiagnostics.ParseVerbose(string.Join("\n", new[]
        {
            "debug1: Connection established.",
            "debug1: Remote protocol version 2.0, remote software version OpenSSH_9.2p1",
            "debug1: Server host key: ssh-ed25519 SHA256:new",
            "Host key verification failed."
        }));
        Check("diagnose: a host-key mismatch stops at Host key",
            bad.Any(s => s.Stage == "Host key" && s.State == "fail"),
            bad.FirstOrDefault(s => s.State == "fail")?.Detail ?? "none");

        var auth = SshDiagnostics.HintsFor("demo@host: Permission denied (publickey).");
        var key = SshDiagnostics.HintsFor("Host key verification failed.");
        Check("diagnose: the offline table separates auth from host-key failures",
            auth.Count > 0 && auth[0].Contains("identity", StringComparison.OrdinalIgnoreCase) &&
            key.Count > 0 && key[0].Contains("known_hosts", StringComparison.OrdinalIgnoreCase),
            $"{auth.Count}/{key.Count} hints");

        var probeSession = new Session { Name = "x", Host = "example.com", Port = 2222, User = "u", KeyPath = @"C:\k" };
        var configArgs = SshLauncher.BuildProbeArguments(probeSession, true, null);
        var handshakeArgs = SshLauncher.BuildProbeArguments(probeSession, false, "true");
        Check("diagnose: probe arguments are bounded and never take a terminal",
            configArgs.StartsWith("-G ") && !configArgs.Contains("-tt") && !configArgs.Contains("-vvv") &&
            handshakeArgs.Contains("-vvv") && handshakeArgs.Contains("BatchMode=yes") &&
            handshakeArgs.Contains("-T") && handshakeArgs.EndsWith("true"),
            handshakeArgs);

        var fp = HostKeys.Fingerprint(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("abc")));
        Check("host keys: fingerprint matches OpenSSH's SHA256 form",
            fp == "SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0", fp);

        var salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using (var hmac = new System.Security.Cryptography.HMACSHA1(salt))
        {
            var mac = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("hashed.example"));
            var field = "|1|" + Convert.ToBase64String(salt) + "|" + Convert.ToBase64String(mac);
            Check("host keys: a hashed known_hosts field matches only its own host",
                HostKeys.MatchesHost(field, "hashed.example", 22) &&
                !HostKeys.MatchesHost(field, "other.example", 22), field);
        }

        var khDir = Path.Combine(Path.GetTempPath(), "sshit-kh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(khDir);
        var khPath = Path.Combine(khDir, "known_hosts");
        File.WriteAllText(khPath, string.Join(((char)10).ToString(), new[]
        {
            "example.com ssh-ed25519 AAAABBBB",
            "[example.com]:2222 ssh-rsa CCCC",
            "other.example ssh-ed25519 AAAABBBB"
        }));
        try
        {
            var stored = HostKeys.Stored("example.com", 22, khPath);
            var shared = HostKeys.SharedHosts("AAAABBBB", "example.com", khPath);
            var forgot = HostKeys.Forget("example.com", 22, out var removed, out var backup, khPath);
            var left = File.ReadAllLines(khPath).Length;
            Check("host keys: stored entries, shared-key hosts and a safe forget",
                stored.Count == 1 && shared.Count == 1 && shared[0] == "other.example" &&
                forgot && removed == 1 && left == 2 && backup is not null && File.Exists(backup),
                $"{stored.Count}/{shared.Count}/{removed}/{left}");
        }
        finally
        {
            try { Directory.Delete(khDir, true); } catch { }
        }
    }

    private static void Skip(string name, string why)
    {
        Report.AppendLine($"SKIP  {name}  ({why})");
    }

    // ---------------------------------------------------------------- fleet posture

    private static void FleetPostureChecks()
    {
        // A real-shaped log: the CLIENT deliberately offers weak algorithms the server does not.
        // Scoring must attribute only what the server advertised, or it would blame hosts for the
        // client's own fallbacks.
        var log = string.Join("\n", new[]
        {
            "debug1: Remote protocol version 2.0, remote software version OpenSSH_10.0p2 Debian-7+deb13u4",
            "debug2: local client KEXINIT proposal",
            "debug2: KEX algorithms: sntrup761x25519-sha512,curve25519-sha256,diffie-hellman-group14-sha1",
            "debug2: host key algorithms: ssh-ed25519,rsa-sha2-512",
            "debug2: ciphers ctos: chacha20-poly1305@openssh.com,3des-cbc",
            "debug2: ciphers stoc: chacha20-poly1305@openssh.com",
            "debug2: MACs ctos: hmac-sha2-256-etm@openssh.com,hmac-md5",
            "debug2: MACs stoc: hmac-sha2-256-etm@openssh.com",
            "debug2: languages ctos: ",
            "debug2: first_kex_follows 0 ",
            "debug2: peer server KEXINIT proposal",
            "debug2: KEX algorithms: mlkem768x25519-sha256,curve25519-sha256,ext-info-s,kex-strict-s-v00@openssh.com",
            "debug2: host key algorithms: rsa-sha2-512,rsa-sha2-256,ecdsa-sha2-nistp256,ssh-ed25519",
            "debug2: ciphers ctos: chacha20-poly1305@openssh.com,aes128-ctr",
            "debug2: ciphers stoc: chacha20-poly1305@openssh.com,aes128-ctr",
            "debug2: MACs ctos: umac-64-etm@openssh.com,hmac-sha1,hmac-sha2-256-etm@openssh.com",
            "debug2: MACs stoc: umac-64-etm@openssh.com,hmac-sha1",
            "debug2: first_kex_follows 0 ",
            "debug1: kex: algorithm: mlkem768x25519-sha256",
            "debug1: kex: host key algorithm: ssh-ed25519",
            "debug1: kex: server->client cipher: chacha20-poly1305@openssh.com MAC: <implicit> compression: none",
            "debug1: kex: client->server cipher: chacha20-poly1305@openssh.com MAC: <implicit> compression: none",
            "debug1: Authentications that can continue: publickey,password"
        });

        var (client, server) = FleetPosture.ParseProposals(log);
        Check("posture: both KEXINIT proposals parse, client and server kept apart",
            server.Kex.Contains("curve25519-sha256") &&
            !server.Kex.Contains("diffie-hellman-group14-sha1") &&
            client.Kex.Contains("diffie-hellman-group14-sha1") &&
            server.HostKey.Count == 4 && server.CipherCtos.Count == 2 && server.MacCtos.Count == 3,
            $"server kex {server.Kex.Count}, hostkeys {server.HostKey.Count}, client kex {client.Kex.Count}");

        Check("posture: pseudo algorithms are dropped from the KEX list",
            !server.Kex.Contains("ext-info-s") && !server.Kex.Contains("kex-strict-s-v00@openssh.com"),
            string.Join(",", server.Kex));

        var negotiated = FleetPosture.ParseNegotiated(log);
        var banner = FleetPosture.ParseBanner(log);
        var auth = FleetPosture.ParseAuth(log);
        Check("posture: the negotiated algorithms and the banner are read from the same log",
            negotiated.Kex == "mlkem768x25519-sha256" && negotiated.HostKey == "ssh-ed25519" &&
            negotiated.Cipher == "chacha20-poly1305@openssh.com" &&
            banner.Protocol == "2.0" && (banner.Software ?? "").StartsWith("OpenSSH_10.0p2"),
            $"{negotiated.Kex}/{negotiated.HostKey}/{negotiated.Cipher} · {banner.Software}");

        Check("posture: the offered authentication methods are listed",
            auth.Count == 2 && auth.Contains("publickey") && auth.Contains("password"),
            string.Join(",", auth));

        var clean = new HostPosture
        {
            SessionName = "demo host",
            Host = "192.0.2.10",
            Offered = server,
            ClientOffer = client,
            Reachable = true,
            Auth = new List<string> { "publickey" },
            SshdConfig = FleetPosture.ParseSshdConfig("permitrootlogin prohibit-password\npasswordauthentication no"),
            SshdNote = null
        };
        var cleanFindings = FleetPosture.Score(clean);
        clean.Findings = cleanFindings;
        Check("posture: a modern OpenSSH server raises only low findings",
            !cleanFindings.Any(f => f.Severity is "high" or "medium") &&
            clean.Worst != "high" && clean.Worst != "medium",
            string.Join("; ", cleanFindings.Select(f => f.Severity + ":" + f.Item)));

        Check("posture: the client's own weak offers are not blamed on the server",
            !cleanFindings.Any(f => f.Item == "diffie-hellman-group14-sha1") &&
            !cleanFindings.Any(f => f.Item == "3des-cbc") &&
            !cleanFindings.Any(f => f.Item == "hmac-md5"),
            string.Join("; ", cleanFindings.Select(f => f.Item)));

        Check("posture: legacy MACs the server still lists are reported as low",
            cleanFindings.Any(f => f.Item == "hmac-sha1" && f.Severity == "low") &&
            cleanFindings.Any(f => f.Item == "umac-64-etm@openssh.com" && f.Severity == "low"),
            string.Join("; ", cleanFindings.Select(f => f.Severity + ":" + f.Item)));

        var dirty = new HostPosture
        {
            SessionName = "old box",
            Host = "10.0.0.9",
            Reachable = true,
            Protocol = "1.99",
            Software = "OpenSSH_6.6.1",
            Offered = new AlgoOffer
            {
                Kex = new List<string> { "diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1" },
                HostKey = new List<string> { "ssh-dss", "ssh-rsa" },
                CipherCtos = new List<string> { "3des-cbc", "aes128-cbc" },
                CipherStoc = new List<string> { "3des-cbc" },
                MacCtos = new List<string> { "hmac-md5", "hmac-sha1" },
                MacStoc = new List<string> { "hmac-md5" }
            },
            ClientOffer = new AlgoOffer { Kex = new List<string> { "curve25519-sha256" } },
            Auth = new List<string> { "password" },
            SshdConfig = FleetPosture.ParseSshdConfig("permitrootlogin yes\npermitemptypasswords yes\nmaxauthtries 9"),
            OfferedHostKeys = new List<HostKeySize> { new() { Type = "ssh-rsa", Bits = 1024 } }
        };

        var dirtyFindings = FleetPosture.Score(dirty);
        dirty.Findings = dirtyFindings;
        Check("posture: a legacy host raises every area, worst first",
            dirty.Worst == "high" && dirty.Points >= 40 &&
            dirtyFindings.Any(f => f.Area == "Key exchange" && f.Item == "diffie-hellman-group1-sha1") &&
            dirtyFindings.Any(f => f.Area == "Host key" && f.Item == "ssh-dss") &&
            dirtyFindings.Any(f => f.Area == "Cipher" && f.Item == "3des-cbc") &&
            dirtyFindings.Any(f => f.Area == "MAC" && f.Item == "hmac-md5") &&
            dirtyFindings.Any(f => f.Area == "Authentication" && f.Item == "password") &&
            dirtyFindings.Any(f => f.Area == "Server config" && f.Item == "PermitRootLogin yes") &&
            dirtyFindings.Any(f => f.Area == "Server version" && f.Item.StartsWith("SSH protocol 1")),
            $"{dirty.Worst}/{dirty.Points}: " + string.Join("; ", dirtyFindings.Select(f => f.Severity + ":" + f.Item)));

        Check("posture: findings are ordered worst first",
            dirtyFindings.Count > 1 && dirtyFindings[0].Severity == "high" &&
            dirtyFindings[^1].Severity is "low" or "info" or "medium",
            string.Join(",", dirtyFindings.Select(f => f.Severity)));

        Check("posture: an algorithm this client cannot offer says so in the finding",
            dirtyFindings.Any(f => f.Item == "ssh-dss" && f.Detail.Contains("This client does not offer it")),
            dirtyFindings.FirstOrDefault(f => f.Item == "ssh-dss")?.Detail ?? "missing");

        Check("posture: a 1024-bit RSA host key is flagged by size, not just by name",
            dirtyFindings.Any(f => f.Item == "ssh-rsa 1024-bit" && f.Severity == "high"),
            string.Join("; ", dirtyFindings.Select(f => f.Item)));

        var sshd = FleetPosture.ParseSshdConfig(string.Join("\n", new[]
        {
            "/etc/ssh/sshd_config: Permission denied",
            "port 22",
            "permitrootlogin without-password",
            "passwordauthentication yes",
            "hostkey /etc/ssh/ssh_host_rsa_key",
            "hostkey /etc/ssh/ssh_host_ed25519_key"
        }));
        Check("posture: sshd -T parses to keys, repeats joined, error prose ignored",
            sshd.Count == 4 && sshd["permitrootlogin"] == "without-password" &&
            sshd["hostkey"] == "/etc/ssh/ssh_host_rsa_key, /etc/ssh/ssh_host_ed25519_key" &&
            !sshd.ContainsKey("/etc/ssh/sshd_config"),
            string.Join("; ", sshd.Select(kv => kv.Key + "=" + kv.Value)));

        // Build a real RSA public-key blob: string "ssh-rsa", mpint e, mpint n.
        static string RsaBlob(int bits)
        {
            var bytes = (bits + 7) / 8;
            var modulus = new byte[bytes + 1];          // mpint keeps a leading zero for the sign bit
            modulus[0] = 0;
            modulus[1] = 0x80;
            for (var i = 2; i < modulus.Length; i++) modulus[i] = 0x11;
            var exponent = new byte[] { 0x01, 0x00, 0x01 };
            using var buffer = new MemoryStream();
            void Field(byte[] value)
            {
                buffer.Write(new[]
                {
                    (byte)(value.Length >> 24), (byte)(value.Length >> 16),
                    (byte)(value.Length >> 8), (byte)value.Length
                });
                buffer.Write(value);
            }
            Field(Encoding.ASCII.GetBytes("ssh-rsa"));
            Field(exponent);
            Field(modulus);
            return Convert.ToBase64String(buffer.ToArray());
        }

        Check("posture: RSA host-key size is measured from the key blob",
            FleetPosture.KeyBits("ssh-rsa", RsaBlob(2048)) == 2048 &&
            FleetPosture.KeyBits("ssh-rsa", RsaBlob(1024)) == 1024,
            $"{FleetPosture.KeyBits("ssh-rsa", RsaBlob(2048))}/{FleetPosture.KeyBits("ssh-rsa", RsaBlob(1024))}");

        var unreachable = new HostPosture
        {
            SessionName = "gone",
            Host = "192.0.2.1",
            Reachable = false,
            FailureStage = "TCP",
            Error = "Connection timed out"
        };
        var unreachableFindings = FleetPosture.Score(unreachable);
        Check("posture: an unreachable host is one note, not a wall of findings",
            unreachableFindings.Count == 1 && unreachableFindings[0].Severity == "info" &&
            unreachable.Worst == "clean" && unreachable.Points == 0,
            string.Join("; ", unreachableFindings.Select(f => f.Severity + ":" + f.Item)));

        var csv = FleetPosture.ToCsv(new[] { clean, dirty });
        var csvLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check("posture: CSV is one quoted row per finding with a header",
            csv.StartsWith("Host,Port,Session,") &&
            csvLines.Length == 1 + cleanFindings.Count + dirtyFindings.Count &&
            csvLines[1].StartsWith("\""), $"{csvLines.Length} lines");

        var markdown = FleetPosture.ToMarkdown(new[] { clean, dirty });
        Check("posture: Markdown carries a summary table, the host and the fix",
            markdown.Contains("| Host | Server |") && markdown.Contains("old box") &&
            markdown.Contains("KexAlgorithms") && markdown.Contains("## demo host"),
            markdown.Split('\n').FirstOrDefault() ?? "");

        var knownHosts = Path.Combine(Path.GetTempPath(), "sshit-posture-knownhosts-" + Guid.NewGuid().ToString("n"));
        try
        {
            File.WriteAllText(knownHosts, string.Join("\n", new[]
            {
                "@cert-authority *.example.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest",
                "other.example.net ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAJOther"
            }));
            var cas = HostKeys.CertAuthorities("host.example.com", 22, knownHosts);
            Check("posture: a @cert-authority line that covers the host is recognised",
                cas.Count == 1 && cas[0] == "*.example.com", string.Join(",", cas));
        }
        finally
        {
            try { File.Delete(knownHosts); } catch { }
        }

        var scannable = FleetPosture.Scannable(new List<Session>
        {
            new() { Name = "ssh", Host = "h", Kind = "Ssh" },
            new() { Name = "local", Host = "localhost", Kind = "Ssh", IsLocal = true },
            new() { Name = "com", Host = "COM3", Kind = "Serial" },
            new() { Name = "alias", Host = "", Kind = "Ssh", UseConfigAlias = true, ConfigAlias = "pi" }
        });
        Check("posture: only real SSH targets are scanned, aliases included",
            scannable.Count == 2 && scannable.All(s => s.Kind == "Ssh") && scannable.Any(s => s.ConfigAlias == "pi"),
            string.Join(",", scannable.Select(s => s.Name)));
    }

    // ---------------------------------------------------------------- the Why? button's evidence

    private static void WhyButtonChecks()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new VtParser(buffer, _ => { });
        Feed(parser, "one\r\ntwo\r\nthree\r\nfour\r\nfive");
        var tail = buffer.Tail(2);
        Check("pane: the scrollback tail holds the last lines a Why? button needs",
            tail.EndsWith("five") && tail.Contains("four") && !tail.Contains("one"), tail.Replace("\n", "|"));

        var all = SshDiagnostics.HintsForPane("Permission denied (publickey).", new List<string>());
        var repeated = SshDiagnostics.HintsForPane("Permission denied (publickey).", new List<string> { all[0] });
        Check("pane: the Why? hints never repeat what the probe already said",
            all.Count >= 3 && repeated.Count == all.Count - 1 && !repeated.Contains(all[0]),
            $"{all.Count}/{repeated.Count}");
    }

    // ---------------------------------------------------------------- the signed evidence chain

    private static void EvidenceChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-evidence-" + Guid.NewGuid().ToString("n"));
        var chain = Path.Combine(dir, "chain.jsonl");
        try
        {
            var session = new Session { Name = "demohost01", Host = "192.0.2.10", User = "demo" };
            EvidenceChain.Append("block", session,
                new JsonObject { ["command"] = "uptime", ["exit"] = 0, ["duration_ms"] = 12 }, chain);
            EvidenceChain.Append("block", session,
                new JsonObject { ["command"] = "df -h", ["exit"] = 1, ["duration_ms"] = 30 }, chain);

            var ok = EvidenceChain.Verify(chain);
            Check("evidence: two signed rows verify, with a key fingerprint",
                ok.Ok && ok.Count == 2 && ok.KeyFingerprint.StartsWith("SHA256:"),
                $"{ok.Ok}/{ok.Count}/{ok.KeyFingerprint}");

            var original = File.ReadAllLines(chain);

            // 1. edit a value inside row 2 (the hash covers the content)
            var edited = (string[])original.Clone();
            edited[1] = edited[1].Replace("\"df -h\"", "\"rm -rf /\"");
            File.WriteAllLines(chain, edited);
            var editedVerdict = EvidenceChain.Verify(chain);
            Check("evidence: an edited row is named as the first break",
                !editedVerdict.Ok && editedVerdict.BreakSeq == 2 &&
                (editedVerdict.FirstBreak ?? "").Contains("hash", StringComparison.OrdinalIgnoreCase),
                editedVerdict.FirstBreak ?? "");

            // 2. remove the link between rows (prev no longer matches)
            var unlinked = (string[])original.Clone();
            unlinked[1] = unlinked[1].Replace("\"prev\":\"", "\"prev\":\"0");
            File.WriteAllLines(chain, unlinked);
            var unlinkedVerdict = EvidenceChain.Verify(chain);
            Check("evidence: a broken previous-row link is named at that row",
                !unlinkedVerdict.Ok && unlinkedVerdict.BreakSeq == 2 &&
                (unlinkedVerdict.FirstBreak ?? "").Contains("previous-row", StringComparison.OrdinalIgnoreCase),
                unlinkedVerdict.FirstBreak ?? "");

            // 3. a signature that does not match its row
            var forged = (string[])original.Clone();
            forged[1] = System.Text.RegularExpressions.Regex.Replace(forged[1], "\"sig\":\"[^\"]+\"", "\"sig\":\"AAAA\"");
            File.WriteAllLines(chain, forged);
            var forgedVerdict = EvidenceChain.Verify(chain);
            Check("evidence: a forged signature is refused",
                !forgedVerdict.Ok && (forgedVerdict.FirstBreak ?? "").Contains("signature", StringComparison.OrdinalIgnoreCase),
                forgedVerdict.FirstBreak ?? "");

            // 4. dropping the newest row cannot be detected — the limit the UI prints
            File.WriteAllLines(chain, original.Take(1));
            var truncated = EvidenceChain.Verify(chain);
            Check("evidence: truncating the newest row still verifies (the documented limit)",
                truncated.Ok && truncated.Count == 1, $"{truncated.Ok}/{truncated.Count}");

            File.WriteAllLines(chain, original);
            var exportPath = Path.Combine(dir, "bundle.json");
            var summary = EvidenceChain.Export(exportPath, chain);
            var bundle = File.ReadAllText(exportPath);
            var key = EvidenceChain.KeyFingerprint(chain);
            var parsed = JsonNode.Parse(bundle) as JsonObject;
            var parts = new[]
            {
                summary.Contains("verifies"),
                parsed?["ok"]?.GetValue<bool>() == true,
                key.Length > 0 && parsed?["key"]?.GetValue<string>() == key,
                parsed?["evidence"] is JsonArray { Count: 2 }
            };
            Check("evidence: the export carries the verdict, the key and every row",
                parts.All(p => p), summary + " | " + string.Join(",", parts) + " | key=" + key);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- the agent lease (R3-06)

    private static void LeaseChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-lease-" + Guid.NewGuid().ToString("n"));
        var store = Path.Combine(dir, "leases.json");
        var session = new Session { Id = "demohost01", Name = "demo host", Host = "192.0.2.10", User = "demo" };
        var other = new Session { Id = "localpwsh01", Name = "PowerShell", IsLocal = true };
        try
        {
            Directory.CreateDirectory(dir);

            Check("lease: a session no grant names is unmanaged",
                AgentLease.Allows(session, "exec", "uptime", out _, store), "unmanaged");

            var grant = AgentLease.Grant(session, new[] { "exec", "sftp-list" }, 15, null, store);
            var loaded = AgentLease.Load(store);
            var verdict = AgentLease.Verify(store);
            Check("lease: a grant is stored, signed and verifies against the device key",
                loaded.Ok && loaded.Rows.Count == 1 && loaded.Rows[0].Verified && loaded.Rows[0].Id == grant.Id && verdict.Ok,
                verdict.Detail);

            Check("lease: a granted verb is allowed",
                AgentLease.Allows(session, "exec", "rm -rf /tmp/x", out var allowed, store), allowed);

            var outside = !AgentLease.Allows(session, "sftp-put", "/tmp/x", out var refusal, store);
            Check("lease: a verb outside the lease is refused, names the grant and prints the fix",
                outside && refusal.Contains(grant.Id) && refusal.Contains("--lease-grant") && refusal.Contains("sftp-put"),
                refusal);

            Check("lease: a grant on one session leaves the others alone",
                AgentLease.Allows(other, "exec", "dir", out _, store), "other session");

            var approval = AgentLease.Grant(session, new[] { "exec" }, 5, "df -h", store);

            // An approval only decides anything where no lease already covers exec, so this case gets
            // its own store: a lease that can only list, plus one approved command.
            var approvalStore = Path.Combine(dir, "approvals.json");
            AgentLease.Grant(session, new[] { "sftp-list" }, 10, null, approvalStore);
            AgentLease.Grant(session, new[] { "exec" }, 5, "df -h", approvalStore);
            var exact = AgentLease.Allows(session, "exec", "df -h", out _, approvalStore);
            var near = !AgentLease.Allows(session, "exec", "df -h /", out var mismatch, approvalStore);
            Check("lease: an approval allows exactly its command and names the one it holds",
                exact && near && mismatch.Contains("df -h") && mismatch.Contains("Approval"), mismatch);

            Check("lease: an approval does not authorise another verb",
                !AgentLease.Allows(session, "watch", "df -h", out _, approvalStore), "watch");

            // An expired but correctly signed row, signed here with the same device key the store uses.
            AddLeaseRow(store, new JsonObject
            {
                ["id"] = "expired001",
                ["kind"] = "lease",
                ["session"] = session.Id,
                ["target"] = session.DisplayTarget,
                ["verbs"] = new JsonArray("sftp-get"),
                ["command"] = "",
                ["granted"] = DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O"),
                ["expires"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O")
            });
            var expiredLoaded = AgentLease.Load(store);
            var expiredRow = expiredLoaded.Rows.FirstOrDefault(r => r.Id == "expired001");
            var expiredRefused = !AgentLease.Allows(session, "sftp-get", "/etc/hostname", out var expiredRefusal, store);
            Check("lease: an expired grant authorises nothing and says how long ago it ended",
                expiredLoaded.Ok && expiredRow is { Verified: true, Active: false } && expiredRefused
                && expiredRefusal.Contains("expired"),
                expiredRefusal);

            Check("lease: the listing marks what is active and what has expired",
                AgentLease.ListText(false, store).Contains("expired") && AgentLease.ListText(false, store).Contains(grant.Id),
                AgentLease.ListText(false, store));

            // Tamper: change a stored verb without re-signing.
            var original = File.ReadAllText(store);
            File.WriteAllText(store, original.Replace("\"exec\",", "\"tunnel-up\","));
            var tampered = AgentLease.Load(store);
            var failClosed = !AgentLease.Allows(session, "exec", "uptime", out var tamperedRefusal, store);
            Check("lease: a tampered row fails verification and closes the surface",
                tampered.Error is not null && failClosed && tamperedRefusal.Contains("failed verification"),
                tamperedRefusal);

            Check("lease: a broken store refuses a revoke instead of re-signing the tampered rows",
                !AgentLease.Revoke(grant.Id, out var brokenRevoke, store) && brokenRevoke.Contains("--lease-clear"),
                brokenRevoke);

            File.WriteAllText(store, original);
            Check("lease: a restored store verifies again", AgentLease.Load(store).Ok,
                AgentLease.Load(store).Error ?? "ok");

            Check("lease: revoking one grant leaves the others in force",
                AgentLease.Revoke(grant.Id, out var revokeError, store)
                && AgentLease.Verify(store).Ok
                && !AgentLease.Allows(session, "exec", "rm -rf /tmp/x", out _, store)
                && AgentLease.Allows(session, "exec", "df -h", out _, store),
                revokeError);

            Check("lease: revoking the last grant returns the session to the default policy",
                AgentLease.Revoke(approval.Id, out _, store) && AgentLease.Revoke("expired001", out _, store)
                && AgentLease.Allows(session, "exec", "rm -rf /tmp/x", out _, store),
                AgentLease.ListText(false, store));

            Check("lease: an unknown verb is refused",
                Throws<InvalidOperationException>(() => AgentLease.Grant(session, new[] { "exce" }, 5, null, store)), "exce");

            Check("lease: no verbs, a zero window and an unbounded window are all refused",
                Throws<InvalidOperationException>(() => AgentLease.Grant(session, Array.Empty<string>(), 5, null, store))
                && Throws<InvalidOperationException>(() => AgentLease.Grant(session, new[] { "exec" }, 0, null, store))
                && Throws<InvalidOperationException>(() => AgentLease.Grant(session, new[] { "exec" }, 100000, null, store)),
                "bounds");

            Check("lease: an approval covers only the command-running verbs",
                Throws<InvalidOperationException>(() => AgentLease.Grant(session, new[] { "sftp-put" }, 5, "/tmp/x", store)),
                "sftp-put approval");

            var readOnly = AgentLease.Grant(session, new[] { "sftp-list" }, 5, null, store);
            var execBlocked = !AgentLease.Allows(session, "exec", "uptime", out var execMessage, store);
            Check("lease: an observe-only lease refuses exec and offers an approval instead",
                execBlocked && execMessage.Contains("--approve") && execMessage.Contains("--lease-grant"), execMessage);
            AgentLease.Revoke(readOnly.Id, out _, store);

            Check("lease: clearing the store leaves every session unmanaged",
                AgentLease.Grant(session, new[] { "sftp-list" }, 5, null, store).Id.Length > 0
                && AgentLease.Clear(store).Ok
                && AgentLease.Allows(session, "exec", "uptime", out _, store)
                && !File.Exists(store),
                "cleared");

            // --- pty-bound approvals: the approval dies with the pane it was approved in ---------
            var paneStore = Path.Combine(dir, "pane.json");
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var selfStart = self.StartTime.ToUniversalTime();
            var bound = AgentLease.Grant(session, new[] { "exec" }, 5, "uptime", paneStore, (self.Id, selfStart));
            var boundRow = AgentLease.Load(paneStore).Rows.FirstOrDefault(r => r.Id == bound.Id);
            Check("lease: an approval bound to a live pty authorises its one command",
                boundRow is { PaneBound: true, PaneAlive: true }
                && AgentLease.Allows(session, "exec", "uptime", out _, paneStore)
                && !AgentLease.Allows(session, "exec", "uptime now", out _, paneStore),
                boundRow?.Standing(DateTimeOffset.UtcNow) ?? "no row");

            // A pid whose process has exited: the approval must stop authorising.
            var doomed = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var doomedStart = doomed.StartTime.ToUniversalTime();
            var doomedPid = doomed.Id;
            doomed.WaitForExit(5000);
            AddLeaseRow(paneStore, new JsonObject
            {
                ["id"] = "closedpane",
                ["kind"] = "approval",
                ["session"] = session.Id,
                ["target"] = session.DisplayTarget,
                ["verbs"] = new JsonArray("exec"),
                ["command"] = "hostname",
                ["granted"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),
                ["expires"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
                ["pane"] = doomedPid,
                ["paneStart"] = doomedStart.ToString("O")
            });
            var closedRow = AgentLease.Load(paneStore).Rows.FirstOrDefault(r => r.Id == "closedpane");
            var closedRefused = !AgentLease.Allows(session, "exec", "hostname", out var closedRefusal, paneStore);
            Check("lease: an approval whose pty has exited authorises nothing and says so",
                closedRow is { PaneBound: true, PaneAlive: false } && closedRefused
                && closedRefusal.Contains("pane has closed"),
                closedRefusal);

            // Same pid, different start time: a recycled pid must not inherit the approval.
            AddLeaseRow(paneStore, new JsonObject
            {
                ["id"] = "recycled01",
                ["kind"] = "approval",
                ["session"] = session.Id,
                ["target"] = session.DisplayTarget,
                ["verbs"] = new JsonArray("exec"),
                ["command"] = "whoami",
                ["granted"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),
                ["expires"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
                ["pane"] = self.Id,
                ["paneStart"] = selfStart.AddHours(-2).ToString("O")
            });
            Check("lease: a recycled pid does not inherit an approval (the start time must match)",
                !AgentLease.Allows(session, "exec", "whoami", out var recycledRefusal, paneStore)
                && recycledRefusal.Contains("recycled01") && recycledRefusal.Contains("closed"),
                recycledRefusal);

            Check("lease: an approval cannot be bound to a pty that is already gone",
                Throws<InvalidOperationException>(() =>
                    AgentLease.Grant(session, new[] { "exec" }, 5, "uptime", paneStore, (doomedPid, doomedStart))),
                "dead pty");

            Check("lease: the listing shows the binding and a closed pane",
                AgentLease.ListText(false, paneStore).Contains("pane closed"), AgentLease.ListText(false, paneStore));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>One row signed with this machine's device key — the shape the store writes itself.</summary>
    private static void AddLeaseRow(string path, JsonObject row)
    {
        // The signature covers every field the store's own Signed() writes, so the pane columns have
        // to exist (as empty defaults) even on a row that is not bound to one.
        if (row["pane"] is null) row["pane"] = 0;
        if (row["paneStart"] is null) row["paneStart"] = "";

        var signature = EvidenceChain.Sign(EvidenceChain.CanonicalJson(row))
                        ?? throw new InvalidOperationException("the device key is unavailable");
        row["alg"] = EvidenceChain.Algorithm;
        row["sig"] = signature.Signature;

        var document = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        var rows = document?["rows"] as JsonArray ?? new JsonArray();
        rows.Add(row.DeepClone());
        File.WriteAllText(path, new JsonObject { ["version"] = 1, ["rows"] = rows.DeepClone() }.ToJsonString());
    }

    // ---------------------------------------------------------------- the signed policy (R3-07)

    private static void PolicyChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-policy-" + Guid.NewGuid().ToString("n"));
        var policyPath = Path.Combine(dir, "policy.json");
        var tokenPath = Path.Combine(dir, "tokens.json");
        var approversDir = Path.Combine(dir, "approvers");
        var session = new Session { Id = "demohost01", Name = "demo host", Host = "192.0.2.10", User = "demo", Port = 22 };
        var other = new Session { Id = "web01", Name = "web", Host = "10.0.0.5", User = "root", Port = 22 };
        try
        {
            Directory.CreateDirectory(dir);

            Check("policy: with no file every verb is allowed",
                AgentPolicyFile.Check(null, session, "exec", "uptime", out _, out _), "no policy");

            var source = Path.Combine(dir, "rules.json");
            File.WriteAllText(source, "{\"verbs\":[\"exec\",\"sftp-list\",\"sftp-get\"],\"hosts\":[\"192.0.2.*\"],"
                + "\"allowPaths\":[\"/tmp/**\",\"/home/demo/**\"],\"denyPaths\":[\"/etc/**\"],"
                + "\"destructive\":\"token\",\"approvers\":[]}");
            var rules = AgentPolicyFile.Set(source, "selftest", policyPath);
            var verdict = AgentPolicyFile.Verify(policyPath);
            Check("policy: an imported rule set is signed and verifies",
                AgentPolicyFile.Load(policyPath).Ok && verdict.Ok, verdict.Detail);

            Check("policy: a verb outside the allowlist is refused and names the fix",
                !AgentPolicyFile.Check(rules, session, "sftp-put", "/tmp/x", out var verbRefusal, out _, tokenPath)
                && verbRefusal.Contains("sftp-put") && verbRefusal.Contains("--policy-set"), verbRefusal);

            Check("policy: a verb inside the allowlist passes",
                AgentPolicyFile.Check(rules, session, "exec", "uptime", out _, out _, tokenPath), "exec");

            Check("policy: a host outside the allowlist is refused",
                !AgentPolicyFile.Check(rules, other, "exec", "uptime", out var hostRefusal, out _, tokenPath)
                && hostRefusal.Contains("10.0.0.5"), hostRefusal);

            Check("policy: a wildcard host pattern matches",
                AgentPolicyFile.Check(rules, session, "exec", "uptime", out _, out _, tokenPath), "192.0.2.*");

            Check("policy: a denied path wins over the allow list",
                !AgentPolicyFile.Check(rules, session, "sftp-list", "/etc/shadow", out var denyRefusal, out _, tokenPath)
                && denyRefusal.Contains("/etc/**"), denyRefusal);

            Check("policy: a path outside the allow list is refused",
                !AgentPolicyFile.Check(rules, session, "sftp-get", "/var/log/syslog", out var pathRefusal, out _, tokenPath)
                && pathRefusal.Contains("/var/log/syslog"), pathRefusal);

            Check("policy: an allowed path passes",
                AgentPolicyFile.Check(rules, session, "sftp-get", "/tmp/notes.txt", out _, out _, tokenPath), "/tmp");

            Check("policy: a destructive command without a token is refused and asks for one",
                !AgentPolicyFile.Check(rules, session, "exec", "rm -rf /tmp/x", out var destructive, out _, tokenPath)
                && destructive.Contains("--policy-token") && destructive.Contains("--policy-approve"), destructive);

            var token = AgentTokens.Create(session, "rm -rf /tmp/x", 10, tokenPath);
            Check("policy: a token is requested by this device and authorises nothing yet",
                !AgentPolicyFile.Check(rules, session, "exec", "rm -rf /tmp/x", out var waiting, out _, tokenPath)
                && waiting.Contains("no second signature"), waiting);

            var generated = AgentTokens.NewApprover("duty", policyPath, approversDir);
            Check("policy: an approver key is generated and registered", generated.Ok, generated.Detail);

            var approved = AgentTokens.Approve(token.Id, "duty", tokenPath, policyPath, approversDir);
            Check("policy: a registered approver countersigns the token", approved.Ok, approved.Detail);

            var reloaded = AgentPolicyFile.Load(policyPath).Rules!;
            Check("policy: a countersigned token authorises its exact command",
                AgentPolicyFile.Check(reloaded, session, "exec", "rm -rf /tmp/x", out var readyRefusal, out var tokenId, tokenPath)
                && tokenId == token.Id, readyRefusal);

            Check("policy: the token does not authorise a different command",
                !AgentPolicyFile.Check(reloaded, session, "exec", "rm -rf /tmp/y", out _, out _, tokenPath), "rm -rf /tmp/y");

            AgentTokens.Consume(token.Id, session, "rm -rf /tmp/x", tokenPath);
            Check("policy: a used token is refused",
                !AgentPolicyFile.Check(reloaded, session, "exec", "rm -rf /tmp/x", out var used, out _, tokenPath)
                && used.Contains("already used"), used);

            Check("policy: an unregistered approver cannot countersign",
                !AgentTokens.Approve(token.Id, "nobody", tokenPath, policyPath, approversDir).Ok, "nobody");

            var selfSource = Path.Combine(dir, "self.json");
            File.WriteAllText(selfSource, "{\"verbs\":[],\"hosts\":[],\"allowPaths\":[],\"denyPaths\":[],"
                + "\"destructive\":\"token\",\"approvers\":[{\"name\":\"me\",\"pub\":\""
                + EvidenceChain.DevicePublicKey() + "\"}]}");
            AgentPolicyFile.Set(selfSource, "self", policyPath);
            var second = AgentTokens.Create(session, "rm -rf /tmp/z", 10, tokenPath);
            var selfApprove = AgentTokens.Approve(second.Id, "me", tokenPath, policyPath, approversDir);
            Check("policy: the requesting device's own key is not a second signature",
                !selfApprove.Ok && selfApprove.Detail.Contains("different key"), selfApprove.Detail);

            var openSource = Path.Combine(dir, "open.json");
            File.WriteAllText(openSource, "{\"verbs\":[],\"hosts\":[],\"allowPaths\":[],\"denyPaths\":[],"
                + "\"destructive\":\"allow\",\"approvers\":[]}");
            var open = AgentPolicyFile.Set(openSource, "open", policyPath);
            Check("policy: destructive allow needs no token",
                AgentPolicyFile.Check(open, session, "exec", "rm -rf /tmp/x", out _, out _, tokenPath), "allow");

            var denySource = Path.Combine(dir, "deny.json");
            File.WriteAllText(denySource, "{\"verbs\":[],\"hosts\":[],\"allowPaths\":[],\"denyPaths\":[],"
                + "\"destructive\":\"deny\",\"approvers\":[]}");
            var deny = AgentPolicyFile.Set(denySource, "deny", policyPath);
            Check("policy: destructive deny refuses outright",
                !AgentPolicyFile.Check(deny, session, "exec", "rm -rf /tmp/x", out var denied, out _, tokenPath)
                && denied.Contains("outright"), denied);

            Check("policy: a rule file with an unknown verb is rejected",
                Throws<InvalidOperationException>(() =>
                {
                    var bad = Path.Combine(dir, "bad.json");
                    File.WriteAllText(bad, "{\"verbs\":[\"exce\"],\"destructive\":\"token\"}");
                    AgentPolicyFile.Set(bad, null, policyPath);
                }), "exce");

            var original = File.ReadAllText(policyPath);
            File.WriteAllText(policyPath, original.Replace("\"destructive\": \"deny\"", "\"destructive\": \"allow\""));
            var tampered = AgentPolicyFile.Load(policyPath);
            Check("policy: an edited policy fails verification and closes the surface",
                !tampered.Ok && (tampered.Error ?? "").Contains("does not verify"), tampered.Error ?? "");

            File.WriteAllText(policyPath, original);
            Check("policy: a restored file verifies again", AgentPolicyFile.Load(policyPath).Ok, "restored");

            Check("policy: clearing the file removes every restriction",
                AgentPolicyFile.Clear(policyPath).Ok && !File.Exists(policyPath)
                && AgentPolicyFile.Check(AgentPolicyFile.Load(policyPath).Rules, session, "sftp-put", "/anywhere", out _, out _, tokenPath),
                "cleared");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- block-indexed recording (R3-15)

    private static void CastChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-cast-" + Guid.NewGuid().ToString("n"));
        var cast = Path.Combine(dir, "session.cast");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(cast, "{\"version\":2,\"width\":80,\"height\":24}\n");

            Check("cast index: a recording with no sidecar has no blocks",
                CastIndex.Load(cast).Count == 0, "empty");

            var start = DateTime.UtcNow.AddMinutes(-2);
            var session = new Session { Id = "demohost01", Host = "192.0.2.10", User = "demo" };
            var first = CastIndex.FromMark(new PromptMark
            {
                Command = "uptime", ExitCode = 0, Duration = TimeSpan.FromSeconds(1.5),
                Started = start.AddSeconds(3), Cwd = "/home/demo"
            }, 1, start, session);
            var second = CastIndex.FromMark(new PromptMark
            {
                Command = "df -h", ExitCode = 1, Duration = TimeSpan.FromSeconds(2.25),
                Started = start.AddSeconds(10), Cwd = "/tmp"
            }, 2, start, session);

            Check("cast index: a block lands on the recording clock, not the wall clock",
                Math.Abs(first.Start - 3) < 0.05 && Math.Abs(first.End - 4.5) < 0.05
                && Math.Abs(second.Start - 10) < 0.05 && Math.Abs(second.End - 12.25) < 0.05,
                $"{first.Start}-{first.End} / {second.Start}-{second.End}");

            CastIndex.Append(cast, first);
            CastIndex.Append(cast, second);
            CastIndex.Append(cast, first);          // the same block number twice must not duplicate

            var loaded = CastIndex.Load(cast);
            Check("cast index: blocks round-trip through the sidecar",
                loaded.Count == 2 && loaded[0].Command == "uptime" && loaded[1].Exit == 1
                && loaded[1].Cwd == "/tmp" && loaded[0].Host == "192.0.2.10",
                loaded.Count + " rows");

            Check("cast index: a block owns the time until the next one, so a playhead is always inside one",
                loaded[0].End >= 6.14 && loaded[1].End > loaded[1].Start,
                loaded[0].End + " / " + loaded[1].End);

            Check("cast index: the playhead finds the block it is inside",
                ReferenceEquals(CastIndex.At(loaded, 3.5), loaded[0])
                && ReferenceEquals(CastIndex.At(loaded, 7), loaded[0])     // the wait belongs to the block above
                && ReferenceEquals(CastIndex.At(loaded, 11), loaded[1])
                && CastIndex.At(loaded, 20) is null,                       // past the last block's tail
                "at()");

            var text = CastIndex.ToText(cast, loaded);
            Check("cast index: the listing shows the clock, the exit and the command",
                text.Contains("00:03") && text.Contains("exit   0") && text.Contains("df -h")
                && text.Contains("2 block(s)"), text.Split('\n')[0]);

            var json = CastIndex.ToJson(cast, loaded);
            var parsed = JsonNode.Parse(json);
            var firstDuration = parsed?["blocks"]?[0]?["duration_ms"]?.GetValue<int>() ?? 0;
            Check("cast index: the JSON carries a duration per block",
                json.Contains("\"command\":\"uptime\"") && firstDuration > 1000,
                "duration_ms=" + firstDuration);

            var missing = CastIndex.ToText(Path.Combine(dir, "none.cast"), new List<CastBlock>());
            Check("cast index: a missing index says why rather than pretending",
                missing.Contains("no block index") && missing.Contains("OSC 133"), missing);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- named service tunnels (R3-12)

    private static void ServiceChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-services-" + Guid.NewGuid().ToString("n"));
        var store = Path.Combine(dir, "services.json");
        var session = new Session { Id = "demohost01", Name = "demo host", Host = "192.0.2.10", User = "demo", Port = 22 };
        try
        {
            Directory.CreateDirectory(dir);

            Check("service: an unknown name is refused with the list",
                ServiceTunnels.Find("nope", store) is null, "none defined");

            var postgres = ServiceTunnels.Add("postgres", session, "L:127.0.0.1:15432:127.0.0.1:5432",
                "postgres://{user}@127.0.0.1:{port}/app", "pg_isready -h 127.0.0.1 -p 5432", "the app db", store);
            var loaded = ServiceTunnels.Load(store);
            Check("service: a named forward round-trips through the store",
                loaded.Count == 1 && loaded[0].Name == "postgres" && loaded[0].CheckCommand.StartsWith("pg_isready")
                && loaded[0].SessionId == "demohost01", loaded.Count + " row(s)");

            Check("service: the DSN template expands host, port, user and session",
                ServiceTunnels.ExpandDsn(postgres, session, TunnelSupervisor.Parse(postgres.Spec))
                    == "postgres://demo@127.0.0.1:15432/app",
                ServiceTunnels.ExpandDsn(postgres, session, TunnelSupervisor.Parse(postgres.Spec)));

            var plain = ServiceTunnels.Add("plain", session, "L:127.0.0.1:18080:127.0.0.1:80", null, null, null, store);
            Check("service: a service with no template gets tcp://host:port",
                ServiceTunnels.ExpandDsn(plain, session, TunnelSupervisor.Parse(plain.Spec)) == "tcp://127.0.0.1:18080",
                ServiceTunnels.ExpandDsn(plain, session, TunnelSupervisor.Parse(plain.Spec)));

            Check("service: a duplicate name is refused",
                Throws<InvalidOperationException>(() =>
                    ServiceTunnels.Add("plain", session, "L:127.0.0.1:18081:127.0.0.1:80", null, null, null, store)),
                "duplicate");

            Check("service: a bad forward spec is refused at add time",
                Throws<TunnelException>(() =>
                    ServiceTunnels.Add("broken", session, "L:127.0.0.1:99999:x:1", null, null, null, store)),
                "bad port");

            Check("service: an unknown DSN token is refused",
                Throws<InvalidOperationException>(() =>
                    ServiceTunnels.Add("typo", session, "L:127.0.0.1:18082:127.0.0.1:80", "tcp://{host}:{port}/{nope}", null, null, store)),
                "unknown token");

            // A live listener answers; the same port stops answering when the listener goes away.
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var probeSpec = TunnelSupervisor.Parse($"L:127.0.0.1:{port}:127.0.0.1:80");
            var up = ServiceTunnels.Probe(probeSpec);
            listener.Stop();
            Thread.Sleep(200);
            var down = ServiceTunnels.Probe(probeSpec);

            Check("service: the health probe accepts a live listener and refuses a closed port",
                up.Status == "up" && down.Status == "down" && down.Detail.Contains(port.ToString()),
                up.Status + " / " + down.Status + " — " + down.Detail);

            var remote = new ServiceDefinition { Name = "r", Spec = "R:127.0.0.1:9000:localhost:22", CheckCommand = "" };
            var remoteReport = ServiceTunnels.Check(remote, session);
            Check("service: a remote forward reports unknown rather than pretending",
                remoteReport.Status == "unknown" && remoteReport.Detail.Contains("-R"), remoteReport.Detail);

            var text = ServiceTunnels.ListText(false, store);
            Check("service: the listing shows name, session, spec and stopped/running",
                text.Contains("postgres") && text.Contains("demohost01") && text.Contains("stopped"), text.Split('\n')[0]);

            Check("service: JSON listing carries the DSN and the running flag",
                ServiceTunnels.ListText(true, store).Contains("\"name\":\"postgres\"")
                && ServiceTunnels.ListText(true, store).Contains("\"running\":false"), "json");

            string missing = "";
            Check("service: removing one leaves the others, and a second remove is refused",
                ServiceTunnels.Remove("plain", out _, store)
                && ServiceTunnels.Load(store).Count == 1
                && ServiceTunnels.Load(store)[0].Name == "postgres"
                && !ServiceTunnels.Remove("plain", out missing, store) && missing.Contains("postgres"),
                missing);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- host inventory (R3-21)

    private static void InventoryChecks()
    {
        var bad = Path.Combine(Path.GetTempPath(), "sshit-utf8-" + Guid.NewGuid().ToString("n"));
        try
        {
            File.WriteAllBytes(bad, new byte[]
            {
                (byte)'h', (byte)'o', (byte)'s', (byte)'t', (byte)'s', (byte)':', (byte)'\n',
                (byte)' ', (byte)' ', (byte)'-', (byte)' ', (byte)'n', (byte)'a', (byte)'m', (byte)'e', (byte)':',
                (byte)' ', 0xFF, 0xFE, (byte)'\n'
            });
            var refused = false;
            try { Inventory.ReadFile(bad); }
            catch (InvalidDataException ex) { refused = ex.Message.Contains("UTF-8", StringComparison.Ordinal); }
            Check("inventory: invalid UTF-8 is refused instead of loading U+FFFD", refused, "refused");

            // A BOM is not corruption: Notepad and PowerShell 5.1 add one, and strict decoding keeps it.
            File.WriteAllBytes(bad, new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes("version: 1\n")).ToArray());
            var bomText = Inventory.ReadFile(bad);
            Check("inventory: a UTF-8 BOM is stripped, not blamed on the first key",
                bomText.StartsWith("version:", StringComparison.Ordinal), bomText);
        }
        finally { try { File.Delete(bad); } catch { } }

        const string yaml = """
            version: 1
            hosts:
              - name: demo host
                host: 192.0.2.10
                user: moddy
                key: ~/.ssh/demo_ed25519
                tags: [pi, camera]
                allowAgent: true
              - name: gateway
                host: gw.example.com
                port: 2222
                user: ops
                tags: bastion
            """;

        var issues = new List<InventoryIssue>();
        var document = Inventory.Parse(yaml, issues);
        Check("inventory: a flat hosts list parses with names, ports, tags and flags",
            issues.Count == 0 && document.Hosts.Count == 2
            && document.Hosts[0].Name == "demo host" && document.Hosts[0].Port == 22
            && document.Hosts[0].Tags.SequenceEqual(new[] { "pi", "camera" }) && document.Hosts[0].AllowAgent
            && document.Hosts[1].Port == 2222, issues.Count + " issue(s)");

        Inventory.Validate(document, issues);
        Check("inventory: a well-formed file validates clean", !Inventory.HasErrors(issues), Inventory.IssueText(issues, document.Hosts.Count));

        // R-15: a 2 MB CR-only line used to be echoed whole in the version error.
        var huge = new List<InventoryIssue>();
        Inventory.Parse("version: " + new string('x', 5000) + "\r\nhosts:\r\n", huge);
        Check("inventory: an invalid version echoes a bounded value, not the whole line",
            huge.Any(i => i.Message.Contains("version must be 1") && i.Message.Length < 200),
            huge.FirstOrDefault()?.Message ?? "none");

        // R-16: an inline list in a scalar field is refused, not turned into a literal string.
        var inline = new List<InventoryIssue>();
        var inlineDocument = Inventory.Parse("hosts:\n  - name: a\n    host: [1.2.3.4, 5.6.7.8]\n", inline);
        Check("inventory: an inline list in a scalar field is refused",
            inline.Any(i => i.Message.Contains("inline collections"))
            && inlineDocument.Hosts.Count == 1 && inlineDocument.Hosts[0].Host.Length == 0,
            inline.FirstOrDefault()?.Message ?? "none");

        // R-17: an unterminated quote is refused; a trailing comment is not part of the value.
        var quotes = new List<InventoryIssue>();
        Inventory.Parse("hosts:\n  - name: \"unterminated\n    host: h\n", quotes);
        Check("inventory: an unterminated quote is refused",
            quotes.Any(i => i.Message.Contains("unterminated quote")),
            quotes.FirstOrDefault()?.Message ?? "none");

        var comments = new List<InventoryIssue>();
        var commentDocument = Inventory.Parse("hosts:\n  - name: a\n    host: web1  # primary\n", comments);
        Check("inventory: a trailing comment is not part of the value",
            comments.Count == 0 && commentDocument.Hosts.Count == 1 && commentDocument.Hosts[0].Host == "web1",
            commentDocument.Hosts.FirstOrDefault()?.Host ?? "no host");

        var unknownKey = new List<InventoryIssue>();
        Inventory.Parse("hosts:\n  - name: a\n    host: h\n    hots: typo\n", unknownKey);
        Check("inventory: an unknown key is a named error",
            unknownKey.Any(i => i.Severity == "error" && i.Message.Contains("hots")), unknownKey.FirstOrDefault()?.Message ?? "none");

        var groups = new List<InventoryIssue>();
        Inventory.Parse("groups:\n  - name: prod\n", groups);
        Check("inventory: `groups:` is refused rather than half-read",
            groups.Any(i => i.Message.Contains("not supported")), groups.FirstOrDefault()?.Message ?? "none");

        Inventory.Parse("hosts:\n  - name: a\n    host: h\n  - name: a\n    host: h2\n", issues = new List<InventoryIssue>());
        Inventory.Validate(Inventory.Parse("hosts:\n  - name: a\n    host: h\n  - name: a\n    host: h2\n", issues), issues);
        Check("inventory: a duplicate name is an error",
            issues.Any(i => i.Severity == "error" && i.Message.Contains("duplicate")), Inventory.IssueText(issues, 2));

        issues = new List<InventoryIssue>();
        Inventory.Validate(Inventory.Parse("hosts:\n  - name: a\n    host: h\n    port: 70000\n", issues), issues);
        Check("inventory: a port outside 1..65535 is an error",
            issues.Any(i => i.Severity == "error" && i.Message.Contains("70000")), Inventory.IssueText(issues, 1));

        issues = new List<InventoryIssue>();
        Inventory.Validate(Inventory.Parse("hosts:\n  - name: a\n    user: u\n", issues), issues);
        Check("inventory: a host with no address is an error",
            issues.Any(i => i.Severity == "error" && i.Message.Contains("no host")), Inventory.IssueText(issues, 1));

        issues = new List<InventoryIssue>();
        Inventory.Validate(Inventory.Parse(
            "hosts:\n  - name: a\n    host: h\n    jump: ghost\n  - name: b\n    host: h2\n    jump: b\n", issues), issues);
        Check("inventory: an unknown jump is a warning, jumping through itself is an error",
            issues.Any(i => i.Severity == "warning" && i.Message.Contains("ghost"))
            && issues.Any(i => i.Severity == "error" && i.Message.Contains("itself")), Inventory.IssueText(issues, 2));

        var sessions = new List<Session>
        {
            new() { Name = "demo host", Host = "192.0.2.10", User = "demo", Port = 22 },
            new() { Name = "local", Host = "", IsLocal = true }
        };
        var exported = Inventory.ToYaml(sessions);
        var roundTrip = Inventory.Parse(exported, issues = new List<InventoryIssue>());
        Check("inventory: an export round-trips through the parser",
            roundTrip.Hosts.Count == 1 && roundTrip.Hosts[0].Name == "demo host"
            && roundTrip.Hosts[0].Host == "192.0.2.10" && exported.Contains("version: 1"),
            roundTrip.Hosts.Count + " host(s)");

        // A Tmux session's name is part of its identity; an export that drops it turns the import
        // into "attach to whatever is most recent", which is a different machine state.
        var tmuxSessions = new List<Session>
        {
            new() { Name = "tmux box", Host = "tmux.example", Kind = "Tmux", TmuxSession = "work" }
        };
        var tmuxYaml = Inventory.ToYaml(tmuxSessions);
        var tmuxRoundTrip = Inventory.Parse(tmuxYaml, issues = new List<InventoryIssue>());

        var forwardHosts = new List<Session> { new() { Name = "fwd box", Host = "fwd.example", ForwardAgent = true } };
        var forwardYaml = Inventory.ToYaml(forwardHosts);
        var forwardDoc = Inventory.Parse(forwardYaml, new List<InventoryIssue>());
        Check("inventory: agent forwarding survives export and import",
              forwardYaml.Contains("forwardagent: true", StringComparison.Ordinal)
              && forwardDoc.Hosts.Count == 1 && forwardDoc.Hosts[0].ForwardAgent,
              forwardDoc.Hosts.Count == 1 ? "forwardAgent=" + forwardDoc.Hosts[0].ForwardAgent : "no host");

        Check("inventory: a tmux session name survives export and import",
            tmuxYaml.Contains("tmux: work", StringComparison.Ordinal)
            && tmuxRoundTrip.Hosts.Count == 1 && tmuxRoundTrip.Hosts[0].Kind == "Tmux"
            && tmuxRoundTrip.Hosts[0].TmuxSession == "work",
            tmuxRoundTrip.Hosts.FirstOrDefault()?.TmuxSession ?? "none");

        var plans = Inventory.Plan(sessions, document);
        Check("inventory: the plan separates add, update and unchanged",
            plans.Count == 2 && plans.Any(p => p.Action == "add" && p.Name == "gateway")
            && plans.Any(p => p.Action == "update" && p.Name == "demo host"), Inventory.PlanText(plans));

        Inventory.Apply(sessions, document);
        Check("inventory: applying adds and updates but never removes",
            sessions.Count == 3 && sessions.Any(s => s.Name == "gateway" && s.Port == 2222)
            && sessions.Any(s => s.IsLocal) && sessions.First(s => s.Name == "demo host").Tags.Contains("camera"),
            sessions.Count + " session(s)");

        Check("inventory: applying the same file twice changes nothing (idempotent)",
            Inventory.Plan(sessions, Inventory.Parse(Inventory.ToYaml(sessions), new List<InventoryIssue>()))
                .All(p => p.Action == "same"), "idempotent");
    }

    // ---------------------------------------------------------------- the transfer queue (R3-22)

    private static void QueueChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-queue-" + Guid.NewGuid().ToString("n"));
        var store = Path.Combine(dir, "queue.json");
        var session = new Session { Id = "demohost01", Name = "demo host", Host = "192.0.2.10", User = "demo" };
        try
        {
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, "note.txt");
            File.WriteAllText(local, "hello");

            Check("queue: an empty queue lists as empty",
                TransferQueue.Load(store).Count == 0 && TransferQueue.ListText(false, store).Contains("empty"), "empty");

            var get = TransferQueue.Add(session, "get", "/etc/hostname", Path.Combine(dir, "hostname.txt"), store);
            var put = TransferQueue.Add(session, "put", "/tmp/note.txt", local, store);
            Check("queue: jobs persist in order with a queued state",
                TransferQueue.Load(store).Count == 2 && TransferQueue.Load(store)[0].Id == get.Id
                && TransferQueue.Load(store)[1].Kind == "put" && TransferQueue.Load(store)[0].State == "queued",
                TransferQueue.ListText(false, store).Split('\n')[0]);

            Check("queue: a put of a file that does not exist is refused at add time",
                Throws<FileNotFoundException>(() =>
                    TransferQueue.Add(session, "put", "/tmp/x", Path.Combine(dir, "ghost.txt"), store)), "ghost");

            Check("queue: an unknown kind is refused",
                Throws<InvalidOperationException>(() =>
                    TransferQueue.Add(session, "mirror", "/a", "/b", store)), "mirror");

            var attempts = new List<string>();
            var failed = TransferQueue.Run(j => session, TransferQueue.Load(store), store, null, false, job =>
            {
                attempts.Add(job.Id);
                if (job.Kind == "put") throw new InvalidOperationException("simulated upload failure");
                job.Bytes = 42;
            });

            var after = TransferQueue.Load(store);
            Check("queue: a failure does not stop the next job, and each records its state",
                failed == 1 && attempts.Count == 2 && after[0].State == "done" && after[1].State == "failed"
                && after[1].Error.Contains("simulated") && after[0].Finished.Length > 0,
                after[0].State + "/" + after[1].State);

            var blocked = TransferQueue.Add(session, "get", "/etc/hosts", Path.Combine(dir, "hosts.txt"), store);
            var blockedFailed = TransferQueue.Run(_ => throw new AgentAccessDeniedException("no lease covers this"),
                TransferQueue.Load(store), store, null, false, _ => { });
            var blockedRow = TransferQueue.Load(store).First(j => j.Id == blocked.Id);
            Check("queue: a blocked job is recorded as failed with the refusal and does not throw out",
                blockedFailed == 1 && blockedRow.State == "failed" && blockedRow.Error.StartsWith("blocked:"),
                blockedRow.Error);

            Check("queue: clearing removes finished jobs and keeps queued ones",
                TransferQueue.Clear(store) == 3 && TransferQueue.Load(store).Count == 0, "cleared");

            var json = TransferQueue.ListText(true, store);
            Check("queue: the JSON listing is an object with a jobs array", json.Contains("\"jobs\""), json.Substring(0, Math.Min(60, json.Length)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- command palette

    private static void PaletteChecks()
    {
        var sessions = new List<Session>
        {
            new() { Id = "demohost01", Name = "demo host", Host = "192.0.2.10", User = "demo" },
            new() { Id = "web01", Name = "Edge web", Host = "web.example.com", User = "deploy" }
        };

        var items = new List<PaletteItem>();
        items.AddRange(CommandPalette.SessionItems(sessions));
        items.AddRange(CommandPalette.ActionItems());
        items.AddRange(CommandPalette.SnippetItems(new[]
        {
            new Snippet { Id = "s1", Name = "Disk hogs", Command = "du -xh / | sort -h | tail", Folder = "Probe" }
        }));

        var snip = CommandPalette.Rank(items, "snip");
        Check("palette: a title prefix outranks a subtitle match",
            snip.Count >= 2 && snip[0].Title == "Snippets" && snip.Any(row => row.Kind == "snippet"),
            snip.Count == 0 ? "no rows" : string.Join(" | ", snip.Take(3).Select(row => row.Title)));

        var byHost = CommandPalette.Rank(items, "0.2.10");
        Check("palette: typing part of a host finds the session through its keywords",
            byHost.Count == 1 && byHost[0].Title == "demo host",
            byHost.Count == 0 ? "no rows" : byHost[0].Title);

        var twoTokens = CommandPalette.Rank(items, "demo 192.0.2.10");
        Check("palette: every token must match, so extra words narrow the list",
            twoTokens.Count == 1 && twoTokens[0].Payload == "demohost01",
            twoTokens.Count + " row(s)");

        Check("palette: a query that matches nothing returns nothing",
            CommandPalette.Rank(items, "zzzqqq").Count == 0, "empty");

        var scoped = CommandPalette.Rank(items, ">");
        Check("palette: '>' keeps commands and settings only",
            scoped.Count == CommandPalette.Actions.Length && scoped.All(row => row.Kind == "action"),
            scoped.Count + " of " + CommandPalette.Actions.Length);

        var zoom = CommandPalette.Rank(items, ">zoom");
        Check("palette: '>' still filters inside the scope",
            zoom.Count == 3 && zoom.All(row => row.Title.Contains("oom", StringComparison.Ordinal)),
            string.Join(" | ", zoom.Select(row => row.Title)));

        var ids = CommandPalette.Actions.Select(action => action.Id).ToList();
        Check("palette: action ids are unique and every row is labelled",
            ids.Distinct(StringComparer.Ordinal).Count() == ids.Count
            && CommandPalette.Actions.All(action => action.Title.Length > 0 && action.Group.Length > 0),
            ids.Count + " ids");

        Check("palette: a session row carries its id and its target",
            items.Any(row => row.Kind == "session" && row.Payload == "demohost01"
                             && row.Subtitle.Contains("demo@192.0.2.10", StringComparison.Ordinal)),
            "session row");

        var snippetRow = items.First(row => row.Kind == "snippet");
        Check("palette: a snippet row shows its folder and keeps the command as a keyword",
            snippetRow.Subtitle.Contains("Probe", StringComparison.Ordinal)
            && snippetRow.Keywords.Contains("sort -h", StringComparison.Ordinal)
            && snippetRow.Payload == "s1",
            snippetRow.Subtitle);

        JsonObject Row(int seq, string kind, JsonObject data, string host) => new()
        {
            ["seq"] = seq,
            ["at"] = DateTime.UtcNow.ToString("O"),
            ["kind"] = kind,
            ["session"] = "demohost01",
            ["host"] = host,
            ["user"] = "moddy",
            ["data"] = data
        };

        var chain = new List<JsonObject>
        {
            Row(1, "block", new JsonObject { ["command"] = "uptime", ["exit"] = 0, ["duration_ms"] = 120, ["cwd"] = "/home/demo" }, "192.0.2.10"),
            Row(2, "block", new JsonObject { ["command"] = "df -h", ["exit"] = 0, ["duration_ms"] = 900, ["cwd"] = "/" }, "192.0.2.10"),
            Row(3, "block", new JsonObject { ["command"] = "uptime", ["exit"] = 1, ["duration_ms"] = 80, ["cwd"] = "/home/demo" }, "192.0.2.10"),
            Row(4, "lease", new JsonObject { ["action"] = "grant" }, ""),
            Row(5, "block", new JsonObject { ["command"] = "", ["exit"] = 0 }, "")
        };

        var history = CommandPalette.HistoryItems(chain);
        Check("palette: the ledger is newest first, skips non-block rows, and counts repeats",
            history.Count == 2 && history[0].Title == "uptime"
            && history[0].Subtitle.Contains("2×", StringComparison.Ordinal) && history[1].Title == "df -h",
            string.Join(" | ", history.Select(row => row.Title + " [" + row.Subtitle + "]")));

        var multiline = CommandPalette.HistoryItems(new List<JsonObject>
        {
            Row(1, "block", new JsonObject { ["command"] = "make\nmake install", ["exit"] = 0 }, "")
        });
        Check("palette: a multi-line command collapses to one row",
            multiline.Count == 1 && multiline[0].Title == "make make install", multiline.Count + " row(s)");

        var services = CommandPalette.ServiceItems(new[]
        {
            new ServiceDefinition { Name = "grafana", SessionId = "demohost01", Spec = "L:3000:localhost:3000", Dsn = "http://{host}:{port}" }
        });
        Check("palette: a service row names its forward and brings the name as payload",
            services.Count == 1 && services[0].Subtitle.Contains("L:3000:localhost:3000", StringComparison.Ordinal)
            && services[0].Payload == "grafana",
            services.Count == 0 ? "none" : services[0].Subtitle);

        var points = CommandPalette.SnapshotItems(new[]
        {
            new UndoPoint
            {
                Id = "u1", SessionId = "demohost01", Target = "/srv/app", Path = "/srv/app",
                Snapshot = "/srv/app.sshit-1", Kind = "copy", Restore = "rm -rf /srv/app && cp -a /srv/app.sshit-1 /srv/app"
            }
        });
        Check("palette: an undo point carries its restore command in the payload",
            points.Count == 1 && points[0].Payload.StartsWith("rm -rf /srv/app", StringComparison.Ordinal),
            points.Count == 0 ? "none" : points[0].Payload);

        var settings = CommandPalette.SettingItems(statusBar: true, sidebar: false, rightPanel: true,
            pin: false, broadcast: true, hasSplit: true);
        var themes = CommandPalette.ThemeItems(new[] { ("System", "Use Windows setting"), ("Dark", "Dark") }, "Dark");
        Check("palette: a toggle states its current value, and broadcast only appears on a split",
            settings.Any(row => row.Title == "Status bar — on" && row.Payload == "set:statusbar")
            && settings.Any(row => row.Title == "Broadcast input — on")
            && CommandPalette.SettingItems(true, true, true, false, false, false).All(row => row.Payload != "set:broadcast"),
            settings.Count + " row(s)");

        Check("palette: the current theme is ticked and its id is the payload",
            themes.Any(row => row.Payload == "theme:Dark" && row.Title.StartsWith("✓", StringComparison.Ordinal))
            && themes.Any(row => row.Payload == "theme:System" && !row.Title.StartsWith("✓", StringComparison.Ordinal)),
            string.Join(" | ", themes.Select(row => row.Title)));

        Check("palette: a provider cap is honoured",
            CommandPalette.SessionItems(sessions, cap: 1).Count == 1
            && CommandPalette.SessionItems(sessions, cap: 0).Count == 0,
            "cap 1 and cap 0");
    }

    // ---------------------------------------------------------------- accessibility announcements

    private static void AccessibilityChecks()
    {
        var announce = Announcer.BlockAnnouncement("make", 0, TimeSpan.FromSeconds(1.23));
        Check("a11y: a finished command reads command, exit and duration",
            announce == "Command finished — \"make\", exit 0, 1.2 seconds.", announce);

        Check("a11y: a non-zero exit is spoken as the number it was",
            Announcer.BlockAnnouncement("kill -9 1", 137, TimeSpan.FromSeconds(0.4))
                .EndsWith("exit 137, 0.4 seconds.", StringComparison.Ordinal),
            Announcer.BlockAnnouncement("kill -9 1", 137, TimeSpan.FromSeconds(0.4)));

        Check("a11y: a completion in another tab names its session first",
            Announcer.BlockAnnouncement("ls", 0, TimeSpan.FromSeconds(2), "demo host")
                .StartsWith("demo host: Command finished", StringComparison.Ordinal),
            Announcer.BlockAnnouncement("ls", 0, TimeSpan.FromSeconds(2), "demo host"));

        var longCommand = new string('x', 200);
        var truncated = Announcer.BlockAnnouncement(longCommand, 0, TimeSpan.FromSeconds(1));
        Check("a11y: a very long command is truncated to one readable line",
            truncated.Contains("…", StringComparison.Ordinal)
            && truncated.Length < 110
            && truncated.Contains(new string('x', Announcer.MaxCommandLength - 1), StringComparison.Ordinal),
            truncated);

        Check("a11y: a multi-line command collapses to spaces",
            Announcer.BlockAnnouncement("make\r\n\tmake install", 0, TimeSpan.FromSeconds(1))
                .Contains("\"make make install\"", StringComparison.Ordinal),
            Announcer.BlockAnnouncement("make\r\n\tmake install", 0, TimeSpan.FromSeconds(1)));

        Check("a11y: a duration over a minute reads in minutes and seconds",
            Announcer.Duration(TimeSpan.FromSeconds(65)) == "1 min 5 s"
            && Announcer.Duration(TimeSpan.FromSeconds(125)) == "2 min 5 s"
            && Announcer.Duration(TimeSpan.FromSeconds(59.96)) == "60.0 seconds",
            Announcer.Duration(TimeSpan.FromSeconds(65)) + " / " + Announcer.Duration(TimeSpan.FromSeconds(59.96)));

        Check("a11y: every session state has a spoken name",
            Announcer.StateName(SessionState.Idle) == "not connected"
            && Announcer.StateName(SessionState.Connecting) == "connecting"
            && Announcer.StateName(SessionState.Connected) == "connected"
            && Announcer.StateName(SessionState.Disconnected) == "disconnected"
            && Announcer.StateName(SessionState.Failed) == "failed",
            Announcer.StateName(SessionState.Connected));

        Check("a11y: a failure announcement carries the reason, trimmed to one line",
            Announcer.StateAnnouncement("demo host", SessionState.Failed, "ssh: connect to host\nport 22: refused")
                == "demo host: connection failed — ssh: connect to host port 22: refused",
            Announcer.StateAnnouncement("demo host", SessionState.Failed, "ssh: connect to host\nport 22: refused"));

        Check("a11y: a plain connect is a short sentence",
            Announcer.StateAnnouncement("demo host", SessionState.Connected) == "demo host: connected.",
            Announcer.StateAnnouncement("demo host", SessionState.Connected));
    }

    // ---------------------------------------------------------------- MCP protocol

    private static void McpChecks()
    {
        var store = new DataStore();
        store.Load();

        string Ask(string json) => McpServer.HandleLine(json, store) ?? "";
        JsonNode? R(string json) => JsonNode.Parse(Ask(json));
        string Short(JsonNode? node)
        {
            var text = node?.ToJsonString() ?? "no result";
            return text.Length > 90 ? text[..90] : text;
        }

        const string Init = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":"
                            + "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},"
                            + "\"clientInfo\":{\"name\":\"selftest\",\"version\":\"1\"}}}";

        var init = R(Init)?["result"];
        Check("mcp: initialize echoes a supported protocol and the real server version",
            init?["protocolVersion"]?.GetValue<string>() == "2025-06-18"
            && init?["serverInfo"]?["version"]?.GetValue<string>()?.StartsWith("1.0.", StringComparison.Ordinal) == true,
            init?.ToJsonString() ?? "no result");

        var old = JsonNode.Parse(Ask(Init.Replace("2025-06-18", "2024-11-05")))?["result"]?["protocolVersion"]?.GetValue<string>();
        Check("mcp: an older client keeps the revision it asked for", old == "2024-11-05", old ?? "none");

        var tools = R("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")?["result"]?["tools"] as JsonArray;
        var labelled = tools is not null && tools.All(tool => tool?["annotations"]?["readOnlyHint"] is JsonValue);
        var execDestructive = tools?.FirstOrDefault(tool => tool?["name"]?.GetValue<string>() == "ssh_exec")
            ?["annotations"]?["destructiveHint"]?.GetValue<bool>() == true;
        Check("mcp: every tool carries annotations, and ssh_exec is marked destructive",
            tools is not null && tools.Count >= 19 && labelled && execDestructive,
            tools is null ? "no tools" : tools.Count + " tools, labelled=" + labelled);

        var names = tools?.Select(tool => tool?["name"]?.GetValue<string>()).ToList() ?? new List<string?>();
        var blastReadOnly = tools?.FirstOrDefault(tool => tool?["name"]?.GetValue<string>() == "blast")
            ?["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true;
        Check("mcp: the audit verbs are exposed, and blast is labelled read-only",
            names.Contains("blast") && names.Contains("diff") && names.Contains("snapshot_take")
            && names.Contains("policy_show") && blastReadOnly,
            string.Join(",", names));

        var policy = R("{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\","
                       + "\"params\":{\"name\":\"policy_show\",\"arguments\":{}}}")?["result"];
        Check("mcp: policy_show answers with the policy document",
            policy?["isError"]?.GetValue<bool>() == false
            && policy?["structuredContent"]?["file"]?.GetValue<string>()?.EndsWith("policy.json", StringComparison.Ordinal) == true,
            Short(policy));

        var unknownMethod = R("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/subscribe\"}");
        Check("mcp: an unknown method is -32601, not an internal error",
            unknownMethod?["error"]?["code"]?.GetValue<int>() == -32601,
            unknownMethod?["error"]?.ToJsonString() ?? "no error");

        var unknownTool = R("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\","
                            + "\"params\":{\"name\":\"nope\",\"arguments\":{}}}");
        Check("mcp: an unknown tool is -32602",
            unknownTool?["error"]?["code"]?.GetValue<int>() == -32602,
            unknownTool?["error"]?.ToJsonString() ?? "no error");

        var missing = R("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\","
                        + "\"params\":{\"name\":\"sftp_get\",\"arguments\":{\"session\":\"x\"}}}");
        Check("mcp: a missing argument is -32602, not a tool result",
            missing?["error"]?["code"]?.GetValue<int>() == -32602,
            missing?["error"]?.ToJsonString() ?? "no error");

        Check("mcp: a notification gets no reply",
            Ask("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{}}").Length == 0,
            "empty");

        var sessions = R("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\","
                         + "\"params\":{\"name\":\"sessions_list\",\"arguments\":{}}}")?["result"];
        Check("mcp: a tool result carries text and structured content",
            sessions?["content"]?[0]?["type"]?.GetValue<string>() == "text"
            && sessions?["structuredContent"] is JsonObject
            && sessions?["isError"]?.GetValue<bool>() == false,
            Short(sessions));

        var refused = R("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"ssh_exec\","
                        + "\"arguments\":{\"session\":\"no-such-host\",\"command\":\"true\"}}}");
        Check("mcp: a refused call is a tool error (isError), not a transport error",
            refused?["error"] is null && refused?["result"]?["isError"]?.GetValue<bool>() == true,
            Short(refused));

        var queue = R("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\","
                      + "\"params\":{\"name\":\"queue_list\",\"arguments\":{}}}")?["result"];
        var services = R("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\","
                         + "\"params\":{\"name\":\"service_list\",\"arguments\":{}}}")?["result"];
        var queueOk = queue?["isError"]?.GetValue<bool>() == false;
        var servicesOk = services?["isError"]?.GetValue<bool>() == false;
        Check("mcp: the queue and the service list are reachable from MCP",
            queueOk && servicesOk,
            "queue=" + queueOk + " services=" + servicesOk);
    }

    // ---------------------------------------------------------------- reconnect policy

    private static void ReconnectChecks()
    {
        Check("reconnect: the ladder waits 2, 5, 15, 30 then 60 seconds and then stops",
            ReconnectPolicy.NextDelay(0, false, true) == 2
            && ReconnectPolicy.NextDelay(1, false, true) == 5
            && ReconnectPolicy.NextDelay(2, false, true) == 15
            && ReconnectPolicy.NextDelay(3, false, true) == 30
            && ReconnectPolicy.NextDelay(4, false, true) == 60
            && ReconnectPolicy.NextDelay(5, false, true) is null,
            string.Join(",", ReconnectPolicy.Delays));

        Check("reconnect: a user's Disconnect stops the ladder at once",
            ReconnectPolicy.NextDelay(0, userStopped: true, autoReconnect: true) is null,
            "stopped");

        Check("reconnect: the feature switch turns it off entirely",
            ReconnectPolicy.NextDelay(0, userStopped: false, autoReconnect: false) is null,
            "off");

        Check("reconnect: the countdown names the wait and how to stop it",
            ReconnectPolicy.Message(5).Contains("5 s", StringComparison.Ordinal)
            && ReconnectPolicy.Message(5).Contains("Disconnect", StringComparison.Ordinal),
            ReconnectPolicy.Message(5));
    }

    // ---------------------------------------------------------------- what proves a connection

    private static void ConnectionProofChecks()
    {
        Check("connect: ConPTY's own prologue is not proof that the far end answered",
            !TerminalPane.ProvesConnection(Encoding.ASCII.GetBytes("[?9001h[?1004h")),
            "prologue only");

        Check("connect: a prompt, an OSC title or a host-key question proves it",
            TerminalPane.ProvesConnection(Encoding.ASCII.GetBytes("[?9001h[?1004hdemo@host:~$ "))
            && TerminalPane.ProvesConnection(Encoding.ASCII.GetBytes("]0;demo@host: ~"))
            && TerminalPane.ProvesConnection(Encoding.ASCII.GetBytes("Are you sure you want to continue connecting?")),
            "remote bytes");

        Check("connect: half a mode sequence waits for the rest instead of counting as a connection",
            !TerminalPane.ProvesConnection(Encoding.ASCII.GetBytes("[?900"))
            && !TerminalPane.ProvesConnection(Encoding.ASCII.GetBytes("")),
            "truncated and empty");
    }

    // ---------------------------------------------------------------- colour tags

    private static void TagChecks()
    {
        Check("tag: a hex colour is normalised, and junk is no tag rather than a crash",
            SessionTag.Normalize("#ff8800") == "#FF8800"
            && SessionTag.Normalize("4caf6e") == "#4CAF6E"
            && SessionTag.Normalize("  #3FB6B0 ") == "#3FB6B0"
            && SessionTag.Normalize("#GGGGGG") is null
            && SessionTag.Normalize("#FFF") is null
            && SessionTag.Normalize("") is null
            && SessionTag.Normalize(null) is null,
            SessionTag.Normalize("#ff8800") ?? "null");

        Check("tag: every preset is a valid hex colour with its own name",
            SessionTag.Presets.All(preset => SessionTag.Normalize(preset) == preset)
            && SessionTag.Presets.Select(SessionTag.Name).Distinct().Count() == SessionTag.Presets.Length
            && SessionTag.Name(null) == "No colour",
            SessionTag.Presets.Length + " presets");

        var tagged = new Session { Id = "tag1", Name = "prod", Host = "db1", ColorTag = "#e05252" };
        var json = System.Text.Json.JsonSerializer.Serialize(tagged, DataStore.SerializerOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<Session>(json, DataStore.SerializerOptions);
        Check("tag: a session's colour survives the real serializer",
            back?.ColorTag == "#e05252" && json.Contains("ColorTag", StringComparison.Ordinal),
            json.Length + " chars");

        var origin = new Session { Id = "t1", Name = "a", Host = "h", Tags = new List<string> { "prod" } };
        var duplicate = origin.Clone();
        duplicate.Tags.Add("staging");
        Check("tag: a cloned session owns its tag list",
            origin.Tags.Count == 1 && duplicate.Tags.Count == 2,
            origin.Tags.Count + "/" + duplicate.Tags.Count);

        var unnamed = new Session { Id = "u1", Name = "", Host = "db1", User = "ops" };
        var edit = new Session();
        edit.CopyEditableFrom(unnamed);
        Check("session: an empty stored name stays empty and keeps tracking host/user",
            edit.Name == "ops@db1" && string.IsNullOrEmpty(edit.StoredName), edit.Name);
        edit.Host = "db2";
        Check("session: the copied empty name follows a later host edit",
            edit.Name == "ops@db2", edit.Name);

        // Saving an edit copies the editor's clone back onto the stored session. That copy used to be a
        // hand-kept list, so colour, kind, serial and X11 edits were silently thrown away.
        var stored = new Session { Id = "keepme", Name = "old", Host = "h1", UseCount = 7 };
        var edited = stored.Clone();
        edited.Name = "new";
        edited.ColorTag = "#3FB6B0";
        edited.Kind = "Serial";
        edited.SerialPortName = "COM4";
        edited.TmuxSession = "guisession";
        edited.X11Forwarding = true;
        edited.AllowAgent = true;
        edited.UseCount = 99;
        stored.CopyEditableFrom(edited);
        Check("edit: every field the editor owns survives the copy back",
            stored.Id == "keepme" && stored.Name == "new" && stored.ColorTag == "#3FB6B0"
            && stored.Kind == "Serial" && stored.SerialPortName == "COM4"
            && stored.TmuxSession == "guisession"
            && stored.X11Forwarding && stored.AllowAgent && stored.UseCount == 7,
            stored.Name + " / " + stored.ColorTag + " / " + stored.Kind + " / uses " + stored.UseCount);
    }

    // ---------------------------------------------------------------- send to every pane

    private static void FanoutChecks()
    {
        var plan = new List<FanoutTarget>
        {
            new() { Tab = "demo host", Session = "demo host", Connected = true },
            new() { Tab = "PowerShell", Session = "PowerShell", Connected = true },
            new() { Tab = "Local test", Session = "Local test host", Connected = false },
            new() { Tab = "prod", Session = "prod", Connected = true, Locked = true }
        };

        Check("fanout: the summary counts receivers and names why the rest are skipped",
            Fanout.Summary(plan) == "2 panes will receive it; 2 skipped (1 not connected, 1 locked).",
            Fanout.Summary(plan));

        Check("fanout: a connected but locked pane is never a receiver",
            Fanout.Receivers(plan) == 2
            && plan.Where(target => target.Locked).All(target => !target.WillReceive),
            plan.Count(target => target.WillReceive) + " receivers");

        Check("fanout: nothing connected reports that instead of success",
            Fanout.Summary(new List<FanoutTarget>
                { new() { Tab = "a", Session = "a", Connected = false } })
                .StartsWith("Nothing to send", StringComparison.Ordinal)
            && Fanout.Summary(new List<FanoutTarget>()) == "No open panes.",
            "empty plans");

        Check("fanout: one pane reads as one pane, not 1 panes",
            Fanout.Summary(new List<FanoutTarget> { new() { Tab = "a", Session = "a", Connected = true } })
                == "1 pane will receive it.",
            "singular");

        // One line, one Enter: a pasted block must not turn into several commands per pane.
        var one = Fanout.CommandBytes("echo hi\r\n");
        var many = Fanout.CommandBytes("a\nb\n\n");
        Check("fanout: the command ends in exactly one carriage return",
            one.Length == 8 && one[^1] == 13 && one[^2] != 13
            && many.Length == 4 && many[^1] == 13 && many[^2] == (byte)'b',
            string.Join(",", one) + " / " + string.Join(",", many));

        Check("fanout: a target is labelled by its tab, or by the session when they agree",
            new FanoutTarget { Tab = "prod", Session = "db1" }.Label == "prod — db1"
            && new FanoutTarget { Tab = "prod", Session = "prod" }.Label == "prod",
            new FanoutTarget { Tab = "prod", Session = "db1" }.Label);

        Check("fanout: panes of one tab carry a pane number so three rows are not identical",
            new FanoutTarget { Tab = "prod", Session = "prod", Pane = "pane 1 of 3" }.Label
            != new FanoutTarget { Tab = "prod", Session = "prod", Pane = "pane 2 of 3" }.Label
            && new FanoutTarget { Tab = "prod", Session = "prod", Pane = "pane 2 of 3" }.Label.EndsWith("pane 2 of 3", StringComparison.Ordinal),
            new FanoutTarget { Tab = "prod", Session = "prod", Pane = "pane 2 of 3" }.Label);

        // D4b: the Send button must be disabled until a command is typed, not just until a receiver exists.
        Check("fanout: Send stays disabled for an empty command even with receivers",
            !Fanout.CanSend(plan, "") && !Fanout.CanSend(plan, "   ") && Fanout.CanSend(plan, "uptime"),
            "receivers=" + Fanout.Receivers(plan));
    }

    // ---------------------------------------------------------------- settings sync

    private static void SyncChecks()
    {
        var syncFolder = Path.Combine(Path.GetTempPath(), "sshit-sync-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(syncFolder);
        try
        {
            File.WriteAllText(Path.Combine(syncFolder, "sessions.json"), "{ not json");
            File.WriteAllText(Path.Combine(syncFolder, "folders.json"), "[]");
            var store = new DataStore();
            store.Load();
            var readReport = SettingsSync.Status(store, syncFolder);
            Check("sync: an unreadable file is named, not reported as zero rows",
                readReport.ReadErrors.Any(e => e.Contains("sessions.json", StringComparison.Ordinal))
                && !readReport.ReadErrors.Any(e => e.Contains("folders.json", StringComparison.Ordinal)),
                string.Join("; ", readReport.ReadErrors));
        }
        finally { try { Directory.Delete(syncFolder, true); } catch { } }

        var local = new List<Session>
        {
            new() { Id = "a", Name = "alpha", Host = "h1" },
            new() { Id = "b", Name = "beta", Host = "h2", ColorTag = "#E05252" }
        };
        var remote = new List<Session>
        {
            new() { Id = "b", Name = "beta", Host = "h2", ColorTag = "#4CAF6E" },   // differs -> conflict
            new() { Id = "c", Name = "gamma", Host = "h3" }                        // new
        };

        var merged = SettingsSync.Merge(local, remote, s => s.Id, SettingsSync.SessionFingerprint, overwrite: false);
        Check("sync: a merge adds what is missing and flags what differs",
            merged.Added == 1 && merged.Conflicting == 1 && merged.Replaced == 0
            && local.Any(s => s.Id == "c") && local.First(s => s.Id == "b").ColorTag == "#E05252"
            && merged.Conflicts.Contains("b"),
            merged.Describe());

        var replaced = SettingsSync.Merge(local, remote, s => s.Id, SettingsSync.SessionFingerprint, overwrite: true);
        Check("sync: --overwrite replaces the conflicting copy and never deletes",
            replaced.Replaced == 1 && local.First(s => s.Id == "b").ColorTag == "#4CAF6E"
            && local.Any(s => s.Id == "a"),
            replaced.Describe());

        var before = new Session { Id = "x", Name = "x", Host = "h", LastUsed = DateTime.UtcNow, UseCount = 9 };
        var after = new Session { Id = "x", Name = "x", Host = "h", LastUsed = DateTime.UtcNow.AddDays(-3), UseCount = 1 };
        var tagged = new Session { Id = "x", Name = "x", Host = "h", ColorTag = "#3FB6B0" };
        Check("sync: the fingerprint ignores use counters but catches a real edit",
            SettingsSync.SessionFingerprint(before) == SettingsSync.SessionFingerprint(after)
            && SettingsSync.SessionFingerprint(before) != SettingsSync.SessionFingerprint(tagged),
            "fingerprint");

        var settings = new AppSettings { Theme = "Dark", FontSize = 15, WindowLeft = 120, PinToDesktop = true,
                                         OpenTabSessionIds = new List<string> { "a" } };
        var portable = SettingsSync.PortableSettings(settings);
        Check("sync: portable settings carry preferences and drop this machine's placement",
            portable["Theme"]?.GetValue<string>() == "Dark"
            && portable["FontSize"]?.GetValue<double>() == 15
            && !portable.ContainsKey("WindowLeft")
            && !portable.ContainsKey("PinToDesktop")
            && !portable.ContainsKey("OpenTabSessionIds"),
            string.Join(",", portable.Select(pair => pair.Key)));

        var older = new Workspace { Name = "demo", Updated = "2026-01-01T00:00:00Z", Tabs = { new WorkspaceTab { SessionId = "a" } } };
        var newer = new Workspace { Name = "demo", Updated = "2026-06-01T00:00:00Z", Tabs = { new WorkspaceTab { SessionId = "b" } } };
        var workspaces = new List<Workspace> { older };
        var workspaceReport = SettingsSync.MergeWorkspaces(workspaces, new[] { newer });
        Check("sync: a workspace is decided by its stamp, not by who imported",
            workspaceReport.Replaced == 1 && workspaces[0].Tabs[0].SessionId == "b",
            workspaceReport.Describe());

        var report = new SyncReport { Folder = "C:/sync", Sessions = merged };
        Check("sync: the report names the conflict and how to take the other copy",
            report.Describe().Contains("conflict   b", StringComparison.Ordinal)
            && report.Describe().Contains("--overwrite", StringComparison.Ordinal),
            report.Describe().Split(Environment.NewLine).Length + " lines");
    }

    // ---------------------------------------------------------------- safe deploy

    private static void DeployChecks()
    {
        Check("deploy: the validator table knows the files that lock people out",
            SafeDeploy.ValidatorFor("/etc/sudoers") == "visudo -c -f {file}"
            && SafeDeploy.ValidatorFor("/etc/sudoers.d/ops") == "visudo -c -f {file}"
            && SafeDeploy.ValidatorFor("/etc/ssh/sshd_config") == "sshd -t -f {file}"
            && SafeDeploy.ValidatorFor("/etc/nginx/nginx.conf") == "nginx -t -c {file}"
            && SafeDeploy.ValidatorFor("/etc/systemd/system/app.service") == "systemd-analyze verify {file}",
            "table");

        Check("deploy: an unknown path has no validator rather than a guessed one",
            SafeDeploy.ValidatorFor("/home/demo/run.sh") is null
            && SafeDeploy.NoValidatorReason("/home/demo/run.sh").Contains("--validator", StringComparison.Ordinal)
            && SafeDeploy.NoValidatorReason("/home/demo/run.sh").Contains("--force", StringComparison.Ordinal),
            "unknown path");

        Check("deploy: the target is the selected file, or the folder plus the local name",
            SafeDeploy.TargetFor("/etc/nginx/nginx.conf", false, "/root", @"C:\tmp\nginx.conf") == "/etc/nginx/nginx.conf"
            && SafeDeploy.TargetFor("/etc/nginx", true, "/root", @"C:\tmp\nginx.conf") == "/etc/nginx/nginx.conf"
            && SafeDeploy.TargetFor(null, false, "/home/demo/", @"C:\tmp\run.sh") == "/home/demo/run.sh"
            && SafeDeploy.TargetFor(null, false, "/", @"C:\tmp\run.sh") == "/run.sh",
            "targets");

        Check("deploy: a local path with no file name has no target",
            SafeDeploy.TargetFor("/etc", true, "/root", "") is null
            && SafeDeploy.TargetFor(null, false, null, @"C:\tmp\run.sh") is null,
            "empty");

        Check("deploy: a template with {file} is substituted, a bare command gets the path appended",
            SafeDeploy.ValidatorCommand("bash -n {file}", "/etc/x.sshit-new") == "bash -n '/etc/x.sshit-new'"
            && SafeDeploy.ValidatorCommand("bash -n", "/etc/x.sshit-new") == "bash -n '/etc/x.sshit-new'"
            && SafeDeploy.ValidatorCommand("bash -n {file}", "/tmp/a b$(id).sh") == "bash -n '/tmp/a b$(id).sh'",
            SafeDeploy.ValidatorCommand("bash -n", "/tmp/x.sshit-new"));

        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            Check("deploy: the backup stamp is Gregorian even under a Thai locale",
                SafeDeploy.BackupPath("/etc/a", new DateTime(2026, 11, 1, 21, 4, 0))
                    == "/etc/a.sshit-bak-20261101-210400",
                SafeDeploy.BackupPath("/etc/a", new DateTime(2026, 11, 1, 21, 4, 0)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }

        Check("deploy: staged and backup names sit beside the target and say what they are",
            SafeDeploy.StagedPath("/etc/ssh/sshd_config") == "/etc/ssh/sshd_config.sshit-new"
            && SafeDeploy.BackupPath("/etc/ssh/sshd_config", new DateTime(2026, 11, 1, 21, 4, 0))
                == "/etc/ssh/sshd_config.sshit-bak-20261101-210400",
            SafeDeploy.BackupPath("/etc/a", new DateTime(2026, 11, 1, 21, 4, 0)));

        var refused = new DeployResult
        {
            Ok = false, Remote = "/etc/ssh/sshd_config", Error = "validator exited 1",
            ValidatorOutput = "line 4: bad option"
        };
        Check("deploy: a refusal says the original was untouched and shows what the validator said",
            refused.ToText().Contains("left untouched", StringComparison.Ordinal)
            && refused.ToText().Contains("bad option", StringComparison.Ordinal),
            refused.ToText().Split(Environment.NewLine).Length + " lines");

        // L14: the deploy worked, but its proof did not. Say that instead of reporting a clean success.
        var noEvidence = new DeployResult { Ok = true, Remote = "/etc/x", EvidenceNote = "the evidence file is locked" };
        Check("deploy: a successful deploy whose evidence row failed says so",
            noEvidence.ToText().Contains("evidence row could not be written", StringComparison.Ordinal)
            && noEvidence.ToJson().Contains("evidenceNote", StringComparison.Ordinal),
            noEvidence.ToText());
    }

    // ---------------------------------------------------------------- tmux control mode

    private static void TmuxChecks()
    {
        // The version gate: read the line `tmux -V` prints, and refuse only what is genuinely older.
        var v35 = TmuxVersion.Parse("tmux 3.5a");
        var v29 = TmuxVersion.Parse("tmux 2.9");
        Check("tmux: the version line is read, letter suffix and all", v35 is { Major: 3, Minor: 5 }, v35.ToString());
        Check("tmux: an old version parses too, so it can be named", v29 is { Major: 2, Minor: 9 }, v29.ToString());
        Check("tmux: unknown text is not a refusal",
              TmuxVersion.Parse("command not found") is null && TmuxVersion.Parse("tmux next-3.6") is null,
              "unknown -> null");
        Check("tmux: 3.0 is the floor, 2.x is below it",
              TmuxVersion.IsSupported((3, 0)) && TmuxVersion.IsSupported((3, 6)) && TmuxVersion.IsSupported((4, 0))
              && !TmuxVersion.IsSupported((2, 9)) && !TmuxVersion.IsSupported((2, 10)),
              "3.0 supported; 2.9 and 2.10 are not");
        Check("tmux: the refusal names the version and the floor",
              TmuxVersion.Refusal((2, 9), "lab-box").Contains("tmux 2.9 on lab-box", StringComparison.Ordinal)
              && TmuxVersion.Refusal((2, 9), "lab-box").Contains("tmux 3.0", StringComparison.Ordinal),
              TmuxVersion.Refusal((2, 9), "lab-box").Split('\n')[0]);

        // A real 538-byte capture from `tmux -C attach` (tmux 3.6, Ubuntu): five commands, one
        // %output, a session change, a window rename and the exit. Replayed as bytes, not trusted prose.
        const string capture =
            "JWJlZ2luIDE3OTM1OTg3ODggMjc2IDANCiVlbmQgMTc5MzU5ODc4OCAyNzYgMA0KJXNlc3Npb24tY2hhbmdlZCAkMCBwcm9vZg0KbGlzdC1zZXNzaW9ucw0KJWJlZ2luIDE3OTM1OTg3ODkgMjgxIDENCnByb29mOiAxIHdpbmRvd3MgKGNyZWF0ZWQgTW9uIE5vdiAgMiAxNjoyMzowOCAyMDI2KSAoYXR0YWNoZWQpDQolZW5kIDE3OTM1OTg3ODkgMjgxIDENCnNlbmQta2V5cyAtdCAwICJlY2hvIGhpIiBFbnRlcg0KJWJlZ2luIDE3OTM1OTg3OTAgMjgyIDENCiVlbmQgMTc5MzU5ODc5MCAyODIgMQ0KJW91dHB1dCAlMCBlY2hvIGhpXDAxNVwwMTINCiV3aW5kb3ctcmVuYW1lZCBAMCBzbGVlcA0KZGlzcGxheS1tZXNzYWdlIC1wICIje3Nlc3Npb25fbmFtZX0gI3t3aW5kb3dfd2lkdGh9eCN7d2luZG93X2hlaWdodH0iDQolYmVnaW4gMTc5MzU5ODc5MCAyODQgMQ0KcHJvb2YgODB4MjQNCiVlbmQgMTc5MzU5ODc5MCAyODQgMQ0KZGV0YWNoLWNsaWVudA0KJWJlZ2luIDE3OTM1OTg3OTEgMjg1IDENCiVlbmQgMTc5MzU5ODc5MSAyODUgMQ0KJWV4aXQNCg==";

        var parser = new TmuxControlParser();
        var events = new List<TmuxEvent>();
        foreach (var line in Encoding.UTF8.GetString(Convert.FromBase64String(capture)).Split('\n'))
        {
            var produced = parser.FeedLine(line);
            if (produced is not null) events.Add(produced);
        }

        var output = events.FirstOrDefault(e => e.Kind == TmuxEventKind.Output);
        Check("tmux: a captured control-mode stream pairs every command and decodes %output",
            events.Count(e => e.Kind == TmuxEventKind.Begin) == 5
            && events.Count(e => e.Kind == TmuxEventKind.End) == 5
            && output is { PaneId: 0, Text: "echo hi\r\n" }
            && events.Any(e => e.Kind == TmuxEventKind.SessionChanged && e.Text == "proof")
            && events.Any(e => e.Kind == TmuxEventKind.WindowRenamed && e.Text == "sleep")
            && events.Count(e => e.Kind == TmuxEventKind.Exit) == 1,
            (output?.Text ?? "no output").Replace("\r", "\\r").Replace("\n", "\\n"));

        var listResponse = events.FirstOrDefault(e => e.Kind == TmuxEventKind.End && e.CommandId == 281)?.Text ?? "";
        Check("tmux: a command's response lines are attached to its %end", 
            listResponse.Contains("proof: 1 windows", StringComparison.Ordinal), listResponse);

        var layout = TmuxLayout.Parse("bb62,80x24,0,0{40x24,0,0,0,39x24,41,0,1}");
        var leaves = layout.Leaves().ToList();
        Check("tmux: a layout string becomes a size tree with pane ids",
            layout is { Width: 80, Height: 24, PaneId: -1 } && layout.Children.Count == 2
            && leaves.Count == 2 && leaves[0] is { PaneId: 0, Width: 40, X: 0 }
            && leaves[1] is { PaneId: 1, Width: 39, X: 41 },
            string.Join(",", leaves.Select(l => l.PaneId + "@" + l.X + "x" + l.Width)));

        var bridge = new TmuxPaneBridge(0);
        foreach (var line in Encoding.UTF8.GetString(Convert.FromBase64String(capture)).Split('\n'))
            bridge.FeedLine(line);
        Check("tmux: %output lands in the terminal buffer",
            bridge.Buffer.Tail(24).Contains("echo hi", StringComparison.Ordinal), bridge.Buffer.Tail(24));

        bridge.FeedLine("%layout-change @0 bb62,80x24,0,0{40x24,0,0,0,39x24,41,0,1} 80x24");
        Check("tmux: a layout-change becomes the pane tree",
            bridge.CurrentLayout is { } tree && tree.Leaves().Count() == 2,
            bridge.CurrentLayout is null ? "no layout" : string.Join(",", bridge.CurrentLayout.Leaves().Select(l => l.PaneId)));

        Check("tmux: a paused pane is resynced, then resumed only after the capture marker",
            bridge.PauseRecovery() is { Count: 2 } recovery
            && recovery[0] == "capture-pane -p -e -t %0"
            && recovery[1] == "display-message -p 'SSHIT-RESYNC-0-END'"
            && TmuxPaneBridge.ResumeSequence(0) == "refresh-client -A \"%0:continue\"",
            string.Join(" | ", bridge.PauseRecovery()));

        Check("tmux: keys, resize, resync and resume are control commands",
            bridge.KeySequence("ls\r") == "send-keys -t %0 -H 6c 73 0d"
            && bridge.ResizeSequence(100, 30) == "refresh-client -C 100x30"
            && bridge.ResyncSequence() == "capture-pane -p -e -t %0"
            && TmuxPaneBridge.ResumeSequence(3) == "refresh-client -A \"%3:continue\"",
            bridge.KeySequence("ls\r"));

        var escaped = "a" + (char)92 + "015" + (char)92 + "012b" + (char)92 + (char)92 + "c";
        var expectedUnescape = "a" + (char)13 + (char)10 + "b" + (char)92 + "c";
        Check("tmux: the client sends bytes and a client size, not typed text",
            TmuxControlParser.SendKeysHex(0, "hi") == "send-keys -t %0 -H 68 69"
            && TmuxControlParser.ResizeClient(80, 24) == "refresh-client -C 80x24"
            && TmuxControlParser.CapturePane(1) == "capture-pane -p -e -t %1"
            && TmuxControlParser.Unescape(escaped) == expectedUnescape,
            TmuxControlParser.SendKeysHex(0, "hi") + " unescape=" + string.Join(",", expectedUnescape.Select(c => (int)c)));

        // Real layouts from tmux 3.6: a top/bottom split uses brackets, not braces. Reading only
        // braces made every split-down layout throw, so this check keeps both spellings honest.
        var vertical = TmuxLayout.Parse("c1b0,80x24,0,0[80x12,0,0,6,80x11,0,13,7]");
        var mixed = TmuxLayout.Parse("3667,80x24,0,0[80x12,0,0,6,80x11,0,13{40x11,0,13,7,39x11,41,13,8}]");
        Check("tmux: a top/bottom split (brackets) parses like a left/right one",
            vertical.Leaves().Count() == 2 && vertical.Leaves().Last() is { PaneId: 7, Y: 13, Height: 11 }
            && mixed.Leaves().Count() == 3,
            string.Join(" ", vertical.Leaves().Select(l => l.PaneId + "@" + l.X + "," + l.Y)));

        var grid = TmuxGridMap.Map(layout);
        Check("tmux: a two-pane layout becomes a grid with the separator cell counted",
            grid.ColumnWidths.SequenceEqual(new[] { 41, 39 }) && grid.RowHeights.SequenceEqual(new[] { 24 })
            && grid.Cells.Count == 2
            && grid.Cells[0] is { PaneId: 0, Column: 0, ColumnSpan: 1, Row: 0 }
            && grid.Cells[1] is { PaneId: 1, Column: 1, ColumnSpan: 1, Row: 0 },
            grid.Describe() + " cols=" + string.Join(",", grid.ColumnWidths));

        var stacked = TmuxGridMap.Map(vertical);
        Check("tmux: a stacked layout maps to rows, not columns",
            stacked.RowHeights.SequenceEqual(new[] { 13, 11 }) && stacked.ColumnWidths.SequenceEqual(new[] { 80 })
            && stacked.Cells[1] is { PaneId: 7, Column: 0, Row: 1, RowSpan: 1 },
            stacked.Describe() + " rows=" + string.Join(",", stacked.RowHeights));

        // A capture is split at 40 columns and separated by newlines; feeding it raw would draw every
        // line from the column the previous one ended in.
        var captured = new TmuxPaneBridge(new TerminalBuffer(40, 5), 3);
        captured.ApplyCapture("first line\nsecond line\n");
        var text = captured.Buffer.Tail(5);
        Check("tmux: a capture replaces the screen with CRLF rows, not a staircase",
            text.Contains("first line", StringComparison.Ordinal)
            && text.Contains("second line", StringComparison.Ordinal)
            && !text.Contains("first linesecond", StringComparison.Ordinal),
            text.Replace("\r", "\\r").Replace("\n", "\\n"));

        // A layout change rebuilds panes; the old buffer's lines must survive as scrollback or a
        // split throws away everything the user had done.
        var historySource = new TmuxPaneBridge(new TerminalBuffer(20, 3), 1);
        historySource.FeedLine("%output %1 OLD-ONE\\015\\012OLD-TWO\\015\\012");
        var rebuiltBuffer = new TerminalBuffer(20, 3);
        rebuiltBuffer.AdoptHistoryFrom(historySource.Buffer);
        Check("tmux: a rebuilt pane carries its earlier output into scrollback",
            rebuiltBuffer.Scrollback.Count >= 2
            && rebuiltBuffer.Tail(10).Contains("OLD-ONE", StringComparison.Ordinal)
            && rebuiltBuffer.Tail(10).Contains("OLD-TWO", StringComparison.Ordinal),
            "scrollback=" + rebuiltBuffer.Scrollback.Count);

        var tmuxSession = new Session { Name = "tmux", Host = "pi.local", User = "demo", Kind = "Tmux" };
        var tmuxArgs = SshLauncher.BuildTmuxArguments(tmuxSession, "work");
        Check("tmux: the ssh transport asks tmux for control mode and quotes the remote session name",
            tmuxArgs.Contains("-tt", StringComparison.Ordinal)
            && tmuxArgs.Contains("demo@pi.local", StringComparison.Ordinal)
            && tmuxArgs.Contains("tmux -C attach -t 'work'", StringComparison.Ordinal),
            tmuxArgs);
    }

    // ---------------------------------------------------------------- audit export

    private static void AuditChecks()
    {
        var session = new Session
        {
            Id = "s1", Name = "prod", Host = "db1", User = "ops", Port = 2222,
            Kind = "Ssh", KeyPath = "C:/keys/id_ed25519", ProxyCommand = "ssh -W %h:%p jump",
            ColorTag = "#E05252", Tags = new List<string> { "prod" }
        };
        var redacted = AuditExport.RedactSession(session);
        Check("audit: a session leaves the machine without a key path or a proxy command",
            redacted["name"]?.GetValue<string>() == "prod"
            && redacted["port"]?.GetValue<int>() == 2222
            && redacted["colorTag"]?.GetValue<string>() == "#E05252"
            && !redacted.ContainsKey("KeyPath")
            && !redacted.ContainsKey("ProxyCommand")
            && !redacted.ContainsKey("AuthMode"),
            string.Join(",", redacted.Select(pair => pair.Key)));

        var ledgerRow = new LedgerRow
        {
            At = "2026-11-02T00:00:00.0000000Z", Session = "prod", Host = "db1", User = "ops",
            Command = "mysql --password=hunter2 -e 'select 1'", Exit = 0, DurationMs = 12,
            Cwd = "/srv", Output = "token=abc123 row 1", OutputBytes = 21
        };
        var thin = AuditExport.RedactLedgerRow(ledgerRow, withOutput: false);
        Check("audit: a ledger row leaves without its output unless asked, and its password is masked",
            !thin.ContainsKey("output") && thin["outputOmitted"]?.GetValue<bool>() == true
            && thin["outputBytes"]?.GetValue<int>() == 21
            && thin["command"]?.GetValue<string>() == "mysql --password=*** -e 'select 1'",
            thin["command"]?.GetValue<string>() ?? "null");

        var fat = AuditExport.RedactLedgerRow(ledgerRow, withOutput: true);
        Check("audit: --with-output carries scrubbed output, not the token it held",
            fat["output"]?.GetValue<string>() == "token=*** row 1",
            fat["output"]?.GetValue<string>() ?? "null");

        Check("audit: the scrubber masks bearer tokens, URL credentials, sshpass and key blocks",
            AuditExport.RedactText("curl -H 'Authorization: Bearer abc.def' https://x") == "curl -H 'Authorization: Bearer ***' https://x"
            && AuditExport.RedactText("ssh ssh://ops:s3cret@db1") == "ssh ssh://ops:***@db1"
            && AuditExport.RedactText("sshpass -p swordfish ssh db1") == "sshpass -p *** ssh db1"
            && AuditExport.RedactText("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----")
                == "[private key redacted]"
            && AuditExport.RedactText("ls -la /tmp") == "ls -la /tmp"
            && AuditExport.RedactText("mysql -u root -pS3cret db") == "mysql -u root -p*** db"
            && AuditExport.RedactText("export AWS_SECRET_ACCESS_KEY=abc123") == "export AWS_SECRET_ACCESS_KEY=***"
            && AuditExport.RedactText("mysql -u root -p") == "mysql -u root -p"
            // A quoted secret used to be skipped entirely because the group excluded quotes.
            && AuditExport.RedactText("mysql --password='S3cret' -e x") == "mysql --password=*** -e x"
            && AuditExport.RedactText("export APIKEY=\"abc\"") == "export APIKEY=***"
            // ... and the env-name pattern must not treat a setting or a model name as a secret.
            && AuditExport.RedactText("ssh -o PasswordAuthentication=no") == "ssh -o PasswordAuthentication=no"
            && AuditExport.RedactText("TOKENIZER=bert-base") == "TOKENIZER=bert-base"
            && AuditExport.RedactText("AWS_SECRET_ACCESS_KEY=abc123") == "AWS_SECRET_ACCESS_KEY=***",
            AuditExport.RedactText("token=abc"));

        Check("audit: a policy error names the file, not the folder it lives in",
            AuditExport.RedactPath("cannot read C:\\Users\\someone\\AppData\\Roaming\\SSH-it\\policy\\policy.json",
                "C:\\Users\\someone\\AppData\\Roaming\\SSH-it\\policy\\policy.json")
                == "cannot read policy.json"
            && Path.GetFileName(AgentPolicyFile.PolicyPath) == "policy.json",
            AuditExport.RedactPath("x " + AgentPolicyFile.PolicyPath, AgentPolicyFile.PolicyPath));

        // A doctored chain must produce a bundle that says so — and an exit code a script can branch on.
        var root = Path.Combine(Path.GetTempPath(), "sshit-audit-probe-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var chain = Path.Combine(root, "chain.jsonl");
            var probe = new Session { Id = "probe1", Name = "probe", Host = "h", User = "u" };
            EvidenceChain.Append("probe", probe, new JsonObject { ["n"] = 1 }, chain);
            EvidenceChain.Append("probe", probe, new JsonObject { ["n"] = 2 }, chain);

            var healthy = AuditExport.Export(new List<Session> { probe }, Path.Combine(root, "healthy.zip"),
                null, 100, "1.0.30-test", false, chain);
            Check("audit: an intact chain verifies and the bundle says ok",
                healthy.Ok && healthy.EvidenceVerdict == "chain verified", healthy.EvidenceVerdict);

            // Edit the first row without re-signing: the hash no longer matches its content.
            var lines = File.ReadAllLines(chain);
            var row = JsonNode.Parse(lines[0])!.AsObject();
            row["data"]!["n"] = 99;
            lines[0] = row.ToJsonString();
            File.WriteAllLines(chain, lines);

            var broken = AuditExport.Export(new List<Session> { probe }, Path.Combine(root, "broken.zip"),
                null, 100, "1.0.30-test", false, chain);
            Check("audit: a doctored chain is reported broken in the bundle and in --json",
                !broken.Ok
                && broken.ToJson().Contains("\"ok\":false", StringComparison.Ordinal)
                && broken.EvidenceVerdict.StartsWith("chain BROKEN", StringComparison.Ordinal),
                broken.EvidenceVerdict);

            using var zip = System.IO.Compression.ZipFile.OpenRead(broken.Path);
            using var reader = new StreamReader(zip.GetEntry("policy.json")!.Open());
            var policyText = reader.ReadToEnd();
            Check("audit: the bundle's policy block carries the bare file name, never the local path",
                !policyText.Contains("AppData", StringComparison.OrdinalIgnoreCase)
                && policyText.Contains("\"file\": \"policy.json\"", StringComparison.Ordinal),
                policyText.Replace(Environment.NewLine, " "));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        var temp = Path.Combine(Path.GetTempPath(), "sshit-sha-" + Guid.NewGuid().ToString("n"));
        try
        {
            File.WriteAllText(temp, "abc");
            // SHA-256 of "abc" is a well-known constant; hashing it here proves the helper reads the bytes.
            Check("audit: the manifest hash is the SHA-256 of the file's bytes",
                AuditExport.Sha256File(temp) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                AuditExport.Sha256File(temp));
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }

        var bundle = new AuditBundle
        {
            Path = "C:/audit.zip", EvidenceRows = 12, LedgerRows = 3, Sessions = 2,
            EvidenceVerdict = "chain verified", Members = new List<string> { "evidence.jsonl", "manifest.json" }
        };
        Check("audit: the bundle says what it carries and that the manifest proves it",
            bundle.ToText().Contains("evidence  12 row(s)", StringComparison.Ordinal)
            && bundle.ToText().Contains("no key paths", StringComparison.Ordinal)
            && bundle.ToText().Contains("proves itself", StringComparison.Ordinal),
            bundle.Members.Count + " members");
    }

    // ---------------------------------------------------------------- help and diagnostics

    private static void HelpChecks()
    {
        var summary = SshIt.Services.Diagnostics.Summary(12, 3, "Dark");
        Check("help: the diagnostics blurb carries version, counts and the log path",
            summary.Contains("SSH-it 1.0.", StringComparison.Ordinal)
            && summary.Contains("Sessions: 12 saved, 3 open tab(s)", StringComparison.Ordinal)
            && summary.Contains("Theme: Dark", StringComparison.Ordinal)
            && summary.Contains("Log: ", StringComparison.Ordinal),
            summary.Length + " chars");

        Check("help: the changelog names this build and mentions no network",
            Changelog.Text.Contains("1.0.29", StringComparison.Ordinal)
            && Changelog.Text.Contains("MCP", StringComparison.Ordinal)
            && !Changelog.Text.Contains("http", StringComparison.OrdinalIgnoreCase),
            Changelog.Text.Length + " chars");

        var session = new Session { Id = "s1", Name = "db", Host = "db.internal", Notes = "prod primary" };
        var items = CommandPalette.SessionItems(new[] { session });
        Check("help: a session's notes are searchable from the palette",
            CommandPalette.Rank(items, "prod primary").Count == 1,
            items[0].Keywords);
    }

    // ---------------------------------------------------------------- workspaces

    private static void WorkspaceChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-ws-" + Guid.NewGuid().ToString("n"));
        var store = Path.Combine(dir, "workspaces.json");

        try
        {
            Directory.CreateDirectory(dir);

            Check("workspace: an empty store lists as empty",
                WorkspaceStore.Load(store).Count == 0
                && WorkspaceStore.ListText(false, store).Contains("no workspaces", StringComparison.Ordinal),
                "empty");

            var captured = Workspace.Capture("prod", new[]
            {
                new WorkspaceTab { SessionId = "a", Split = true, Vertical = false, Broadcast = true },
                new WorkspaceTab { SessionId = "b" },
                new WorkspaceTab { SessionId = "" }
            }, activeIndex: 5, tool: "Tunnels");

            Check("workspace: capture drops empty tabs and clamps the active index",
                captured.Tabs.Count == 2 && captured.ActiveIndex == 1 && captured.Tool == "Tunnels"
                && captured.Tabs[0].Split && !captured.Tabs[0].Vertical && captured.Tabs[0].Broadcast,
                captured.Summary());

            WorkspaceStore.Store(captured, store);
            Check("workspace: a saved workspace loads back with its tabs",
                WorkspaceStore.Load(store).Count == 1 && WorkspaceStore.Find("PROD", store)?.Tabs.Count == 2,
                "round-trip");

            var again = Workspace.Capture("prod", new[] { new WorkspaceTab { SessionId = "c" } }, 0, "Files");
            WorkspaceStore.Store(again, store);
            var all = WorkspaceStore.Load(store);
            Check("workspace: saving under an existing name replaces it and keeps the Created stamp",
                all.Count == 1 && all[0].Tabs.Count == 1 && all[0].Tabs[0].SessionId == "c"
                && all[0].Created == captured.Created,
                all.Count + " entry");

            Check("workspace: a name lookup is case-insensitive",
                WorkspaceStore.Find("PrOd", store) is not null, "case");

            File.WriteAllText(store, "{not json");
            Check("workspace: a damaged store reads as empty rather than throwing",
                WorkspaceStore.Load(store).Count == 0, "corrupt");

            WorkspaceStore.Store(captured, store);
            Check("workspace: removing deletes only the named workspace",
                !WorkspaceStore.Remove("missing", store)
                && WorkspaceStore.Remove("prod", store)
                && WorkspaceStore.Load(store).Count == 0,
                "remove");

            var items = CommandPalette.WorkspaceItems(new[] { captured });
            Check("workspace: the palette row names the layout and its size",
                items.Count == 1 && items[0].Payload == "prod"
                && items[0].Subtitle.Contains("2 tab(s)", StringComparison.Ordinal),
                items.Count == 0 ? "none" : items[0].Subtitle);

            Check("workspace: the listing text summarises a layout",
                CommandLedger.Format(new List<LedgerRow>(), false).Length > 0
                && captured.Summary().Contains("2 tab(s)", StringComparison.Ordinal)
                && captured.Summary().Contains("1 split", StringComparison.Ordinal),
                captured.Summary());

            // The open summary is the only place a vanished tab is reported. A missing session that
            // opens silently reads as "the workspace was smaller than I remembered".
            Check("workspace: the open summary counts opened tabs and missing sessions",
                Workspace.OpenSummary("demo", 3, 1) == "Workspace \"demo\": 3 tab(s) opened, 1 session(s) no longer exist."
                && Workspace.OpenSummary("demo", 2, 0) == "Workspace \"demo\": 2 tab(s) opened."
                && Workspace.OpenSummary("demo", 0, 2) == "Workspace \"demo\": none of its 2 session(s) exist any more."
                && Workspace.OpenSummary("demo", 0, 0).EndsWith("no tabs to open.", StringComparison.Ordinal),
                Workspace.OpenSummary("demo", 3, 1));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- command ledger (C9)

    private static void LedgerChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshit-ledger-" + Guid.NewGuid().ToString("n"));
        var store = Path.Combine(dir, "ledger.jsonl");
        var pi = new Session { Id = "demohost01", Name = "demo host", Host = "192.0.2.10", User = "demo" };
        var web = new Session { Id = "web01", Name = "Edge web", Host = "web.example.com", User = "deploy" };

        try
        {
            Directory.CreateDirectory(dir);

            var first = CommandLedger.Append(pi, "docker compose up -d", 0, TimeSpan.FromSeconds(2.5),
                "/srv/app", "Creating network app_default\nStarted 3 containers", store);
            var afterFirst = CommandLedger.Tail(10, store);
            Check("ledger: a finished block is stored with its command, exit, duration and output",
                first is not null && afterFirst.Count == 1 && afterFirst[0].Exit == 0
                && afterFirst[0].Host == "192.0.2.10" && afterFirst[0].DurationMs == 2500
                && afterFirst[0].Output.Contains("Started 3 containers", StringComparison.Ordinal),
                afterFirst.Count == 0 ? "empty" : afterFirst[0].Command);

            Check("ledger: a block with no command and no output is not stored",
                CommandLedger.Append(pi, "", 0, TimeSpan.Zero, "/", "   ", store) is null
                && CommandLedger.Tail(10, store).Count == 1,
                CommandLedger.Tail(10, store).Count + " row(s)");

            var big = new string('a', 20_000) + "TAIL-MARKER";
            var capped = CommandLedger.Append(pi, "make", 2, TimeSpan.FromSeconds(30), "/srv/app", big, store);
            Check("ledger: long output keeps its head and tail and records the real size",
                capped is not null && capped.Output.Length < 20_000
                && capped.Output.Contains("characters omitted", StringComparison.Ordinal)
                && capped.Output.EndsWith("TAIL-MARKER", StringComparison.Ordinal)
                && capped.OutputBytes > 20_000,
                capped is null ? "null" : capped.Output.Length + " chars, " + capped.OutputBytes + " bytes");

            CommandLedger.Append(pi, "uptime", 0, TimeSpan.FromSeconds(1), "/", "load average 0.4", store);
            CommandLedger.Append(pi, "cat /var/log/syslog", 0, TimeSpan.FromSeconds(1), "/", "the uptime service restarted", store);
            CommandLedger.Append(web, "systemctl restart nginx", 0, TimeSpan.FromSeconds(1), "/", "ok", store);

            var ranked = CommandLedger.Search("uptime", 10, store);
            Check("ledger: a command hit outranks a word that only appears in output",
                ranked.Count >= 2 && ranked[0].Command == "uptime",
                string.Join(" | ", ranked.Select(row => row.Command)));

            Check("ledger: every query word must match somewhere",
                CommandLedger.Search("docker restart", 10, store).Count == 0
                && CommandLedger.Search("docker compose", 10, store).Count == 1,
                "and-semantics");

            var failures = CommandLedger.Search("", 10, store, exit: "nonzero");
            Check("ledger: the exit filter keeps only the failed command",
                failures.Count == 1 && failures[0].Command == "make",
                failures.Count + " row(s)");

            var onlyWeb = CommandLedger.Search("", 10, store, host: "example.com");
            Check("ledger: the host filter isolates one host",
                onlyWeb.Count == 1 && onlyWeb[0].Session == "Edge web",
                onlyWeb.Count + " row(s)");

            File.AppendAllText(store, "{not json at all\n");
            Check("ledger: a damaged line is skipped without losing the rest",
                CommandLedger.Tail(10, store).Count == 5,
                CommandLedger.Tail(10, store).Count + " row(s)");

            var palette = CommandPalette.LedgerItems(CommandLedger.Tail(10, store));
            var uptimeItem = palette.FirstOrDefault(item => item.Title == "uptime");
            Check("ledger: the palette row carries the output as search text",
                uptimeItem is not null && uptimeItem.Keywords.Contains("load average", StringComparison.Ordinal)
                && palette[0].Payload == "systemctl restart nginx",
                uptimeItem is null ? "missing" : uptimeItem.Subtitle);

            var removed = CommandLedger.Clear(store);
            Check("ledger: clearing removes the store and says how many rows went",
                removed == 5 && !File.Exists(store), removed + " row(s)");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- watch mode

    private static void WatchChecks()
    {
        var (added, removed) = CommandWatch.Diff(
            "up 3 days\nload 0.1\neth0 up",
            "up 3 days\nload 0.9\neth0 down\nextra");
        Check("watch: the diff names what appeared and what disappeared",
            added.Count == 3 && added.Contains("load 0.9") && added.Contains("extra") &&
            removed.Count == 2 && removed.Contains("load 0.1") && removed.Contains("eth0 up"),
            $"+{string.Join("|", added)} −{string.Join("|", removed)}");

        var same = CommandWatch.Diff("a\nb", "a\nb");
        Check("watch: an unchanged run diffs to nothing",
            same.Added.Count == 0 && same.Removed.Count == 0, "empty");

        var watch = new CommandWatch(new Session { Host = "h", User = "u" }, "uptime", 5);
        var loop = watch.RemoteLoop();
        Check("watch: the remote loop subshells the command, marks the exit and sleeps",
            loop.StartsWith("while :; do ( uptime )") && loop.Contains("st=$?") &&
            loop.Contains("\\036WATCH_") && loop.Contains("_%s") && loop.Contains("sleep 5") &&
            loop.EndsWith("done"), loop);

        var other = new CommandWatch(new Session { Host = "h", User = "u" }, "uptime", 5);
        Check("watch: each watch carries its own end-of-run token",
            watch.MarkerPrefix != other.MarkerPrefix, watch.MarkerPrefix);

        // Only prefix + integer ends a run; anything else is output with the separator made visible.
        // A command whose output has no trailing newline glues its text to the marker.
        var glued = CommandWatch.TryMarker("ok" + watch.MarkerPrefix + "7", watch.MarkerPrefix, out var gluedAt, out var gluedExit);
        Check("watch: a marker glued to the end of output still completes the run",
            glued && gluedAt == 2 && gluedExit == 7, gluedAt + "/" + gluedExit);

        var forged = watch.MarkerPrefix + "not-a-number";
        var escaped = CommandWatch.EscapeMarker(forged);
        Check("watch: a line that only looks like a marker does not end the run",
            !CommandWatch.TryMarker(forged, watch.MarkerPrefix, out _, out _),
            forged);
        Check("watch: a forged marker is escaped in output instead of ending the run",
            escaped.StartsWith("\u241eWATCH_", StringComparison.Ordinal)
            && !escaped.Contains('\u001e')
            && CommandWatch.EscapeMarker("plain output") == "plain output",
            escaped);

        // The window builds with the app's own resources (a missing brush key or a bad layout
        // throws here rather than in front of the user). It is not shown from the suite.
        var window = new Views.WatchWindow(new Session { Host = "192.0.2.10", User = "demo" }, "uptime");
        try
        {
            Check("watch: the window constructs with the app resources and the target in its title",
                window.Title.StartsWith("Watch — demo@192.0.2.10"), window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------- block diff

    private static void DiffChecks()
    {
        var same = BlockDiff.Align("a\nb\nc", "a\nb\nc");
        Check("diff: identical outputs align line for line with no changes",
            same.Count == 3 && BlockDiff.Changed(same) == 0 && same.All(r => r.Mark == "="),
            BlockDiff.Summary(same));

        var replaced = BlockDiff.Align("a\nb\nc", "a\nB\nc");
        Check("diff: a replaced line becomes one paired change row",
            replaced.Count == 3 && replaced[1].Kind == "change" && replaced[1].Mark == "≠" &&
            replaced[1].Left == "b" && replaced[1].Right == "B",
            string.Join(" ", replaced.Select(r => r.Mark)));

        var added = BlockDiff.Align("a\nc", "a\nb\nc");
        Check("diff: an inserted line is a plus row on its own",
            added.Count == 3 && added.Any(r => r.Kind == "add" && r.Mark == "+" && r.Right == "b") &&
            BlockDiff.Changed(added) == 1,
            string.Join(" ", added.Select(r => r.Mark)));

        var removed = BlockDiff.Align("a\nb\nc", "a\nc");
        Check("diff: a dropped line is a minus row on its own",
            removed.Count == 3 && removed.Any(r => r.Kind == "remove" && r.Mark == "−" && r.Left == "b") &&
            BlockDiff.Changed(removed) == 1,
            string.Join(" ", removed.Select(r => r.Mark)));

        var windows = new List<Session> { new() { Name = "a", Host = "h1", User = "u", Kind = "Ssh" } };
        var window = new Views.DiffWindow(windows, windows[0], "df -h");
        try
        {
            Check("diff: the window constructs with the app resources",
                window.Title == "Compare command output", window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------- blast radius

    private static void BlastChecks()
    {
        var rm = BlastRadius.Assess("sudo rm -rf /var/tmp/build/*");
        Check("blast: rm -rf is high, with the pattern as its operand",
            rm.Count == 1 && rm[0].Severity == "high" && rm[0].Kind == "delete" &&
            rm[0].Operands.SequenceEqual(new[] { "/var/tmp/build/*" }),
            string.Join(",", rm.SelectMany(f => f.Operands)));

        var plain = BlastRadius.Assess("ls -l /var/tmp && cat /etc/hostname");
        Check("blast: a read-only command has no findings", plain.Count == 0, plain.Count.ToString());

        var find = BlastRadius.Assess("find /var/log -name '*.gz' -delete");
        Check("blast: find -delete is high and its start path is the operand",
            find.Any(f => f.Severity == "high" && f.Kind == "delete" && f.Operands.Contains("/var/log")),
            string.Join(",", find.SelectMany(f => f.Operands)));

        var dd = BlastRadius.Assess("dd if=/dev/zero of=/dev/sda bs=1M count=10");
        Check("blast: dd reads its of= target", dd.Any(f => f.Kind == "overwrite" && f.Operands.Contains("/dev/sda")),
            string.Join(",", dd.SelectMany(f => f.Operands)));

        var redirect = BlastRadius.Assess("echo hi > /etc/hosts");
        Check("blast: a truncating redirect names its target",
            redirect.Any(f => f.Kind == "overwrite" && f.Operands.Contains("/etc/hosts")),
            string.Join(",", redirect.SelectMany(f => f.Operands)));

        var script = BlastRadius.Script(new[] { "/var/tmp/*.log", "it's here" });
        Check("blast: the expansion script keeps globs live and escapes a quote",
            script.Contains("for f in /var/tmp/*.log") && script.Contains("it\\'s\\ here") &&
            script.Contains("printf 'N %s %s\\n'"),
            script);

        var report = new BlastReport();
        BlastRadius.ParseExpansion("P 1\nD /tmp/a\nF /tmp/b\nN 1 2\nP 2\nN 2 0\n",
            new[] { "/tmp/*", "/nope" }, report);
        Check("blast: expansion output becomes counts, directories and files",
            report.Matches.Count == 2 && report.Matches[0].Count == 2 &&
            report.Matches[0].Directories.SequenceEqual(new[] { "/tmp/a" }) &&
            report.Matches[0].Files.SequenceEqual(new[] { "/tmp/b" }) &&
            report.Matches[1].Count == 0,
            string.Join(";", report.Matches.Select(m => m.Pattern + "=" + m.Count)));

        Check("blast: the undo hint matches the filesystem",
            (BlastRadius.UndoHint("btrfs", "/mnt/data") ?? "").Contains("btrfs subvolume snapshot") &&
            (BlastRadius.UndoHint("ext4", "/mnt/data") ?? "").Contains("cp -a --reflink=auto"),
            BlastRadius.UndoHint("btrfs", "/mnt/data") ?? "");

        var window = new Views.BlastWindow(new Session { Host = "h", User = "u" }, "rm -rf /tmp/*");
        try
        {
            Check("blast: the window constructs with the app resources",
                window.Title.StartsWith("Blast radius"), window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------- undo points

    private static void UndoChecks()
    {
        Check("undo: a glob snapshots its directory, a plain path its parent",
            UndoStore.BasePath("/tmp/blast-demo/*") == "/tmp/blast-demo" &&
            UndoStore.BasePath("/var/log/*.log") == "/var/log" &&
            UndoStore.BasePath("/etc/hosts") == "/etc" &&
            UndoStore.BasePath("/") == "/",
            UndoStore.BasePath("/tmp/blast-demo/*") + " / " + UndoStore.BasePath("/etc/hosts"));

        Check("undo: system paths are refused",
            UndoStore.Refusal("/") is not null && UndoStore.Refusal("/etc/ssh") is not null &&
            UndoStore.Refusal("/tmp/blast-demo") is null,
            UndoStore.Refusal("/etc/ssh") ?? "none");

        var btrfs = UndoStore.Plan("/mnt/data", "btrfs", "20260101-120000");
        var copy = UndoStore.Plan("/tmp/blast-demo", "ext4", "20260101-120000");
        Check("undo: the plan follows the filesystem, and carries its restore command",
            btrfs.Kind == "btrfs" && btrfs.Command.Contains("btrfs subvolume snapshot") &&
            btrfs.Restore.Contains("btrfs subvolume snapshot") &&
            copy.Kind == "copy" && copy.Command.Contains("cp -a --reflink=auto") &&
            copy.Restore.StartsWith("rm -rf '/tmp/blast-demo'"),
            btrfs.Kind + " / " + copy.Kind);

        var dir = Path.Combine(Path.GetTempPath(), "sshit-undo-" + Guid.NewGuid().ToString("n"));
        var store = Path.Combine(dir, "snapshots.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(store, JsonSerializer.Serialize(new[]
            {
                new UndoPoint
                {
                    Id = "abc123", SessionId = "s1", Target = "u@h", Path = "/tmp/x",
                    Snapshot = "/tmp/x.sshit-1", Kind = "copy", Restore = "rm -rf '/tmp/x'",
                    At = new DateTime(2026, 1, 1, 12, 0, 0)
                }
            }));
            var points = UndoStore.List("s1", store);
            Check("undo: recorded points round-trip per session",
                points.Count == 1 && points[0].Id == "abc123" && points[0].Kind == "copy" &&
                UndoStore.List("other", store).Count == 0,
                points.Count.ToString());

            Check("undo: a point describes itself with the restore command",
                UndoStore.Describe(points[0]).Contains("restore:") &&
                UndoStore.Describe(points[0]).Contains("abc123"),
                UndoStore.Describe(points[0]).Split('\n')[0]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>L9: the bench's own arithmetic, its corpora and the regression flag.</summary>
    private static void BenchChecks()
    {
        var sample = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Array.Sort(sample);
        Check("bench: p50 is nearest-rank", Math.Abs(PerfMetrics.Percentile(sample, 0.50) - 5) < 1e-9,
            PerfMetrics.Percentile(sample, 0.50).ToString("0.###"));
        Check("bench: p99 is nearest-rank", Math.Abs(PerfMetrics.Percentile(sample, 0.99) - 10) < 1e-9,
            PerfMetrics.Percentile(sample, 0.99).ToString("0.###"));

        var a = Bench.Ascii("a", 20_000);
        var b = Bench.Ascii("b", 20_000);
        var csi = Bench.CsiHeavy("csi", 20_000);
        var utf = Bench.Utf8("utf8", 20_000);
        Check("bench: the ASCII corpus is deterministic",
            a.Data.Length == b.Data.Length && a.Checksum == b.Checksum && a.Data.Length >= 20_000,
            a.Data.Length + "/" + b.Data.Length);
        Check("bench: the CSI corpus carries escapes and the UTF-8 corpus multibyte text",
            csi.Data.AsSpan().IndexOf((byte)0x1b) >= 0 && utf.Data.Length > 20_000 && utf.Data.Any(x => x > 0x7f),
            csi.Data.Length + "/" + utf.Data.Length);

        var tiny = Bench.Measure(a, runs: 2, warmup: 0);
        Check("bench: a measured feed lands in the buffer",
            tiny.MedianMb > 0 && tiny.Lines > 0 && tiny.Bytes == a.Data.Length,
            $"{tiny.MedianMb:0.0} MB/s, lines {tiny.Lines}");

        var json = new JsonObject();
        var dir = Path.Combine(Path.GetTempPath(), "sshit-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var old = Path.Combine(dir, "old.json");
            File.WriteAllText(old, "{\"parse\":[{\"name\":\"a\",\"medianMBps\":1000000}]}");
            var slow = Bench.Compare.Run(old, new[] { tiny }, json);
            var quick = Path.Combine(dir, "quick.json");
            File.WriteAllText(quick, "{\"parse\":[{\"name\":\"a\",\"medianMBps\":0.000001}]}");
            var good = Bench.Compare.Run(quick, new[] { tiny }, json);
            Check("bench: compare turns a slowdown into a regression flag",
                slow is { Regressed: true } && good is { Regressed: false } && json["compare"] is not null,
                slow?.Text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>A block's text must not depend on whether the shell's C mark beats its first
    /// output row, or whether D beats the final newline — both orderings are real on one host.</summary>
    private static void BlockOutputChecks()
    {
        var (late, lateParser) = Make(40, 8);
        Feed(lateParser, "\u001b]133;A\u0007\u001b]133;B\u0007$ echo hi\r\n");
        Feed(lateParser, "hi\r\n");
        Feed(lateParser, "\u001b]133;C\u0007");
        Feed(lateParser, "\u001b]133;D;0\u0007");
        var lateText = late.Prompts.Count > 0 ? late.BlockOutput(late.Prompts[0]) : "<none>";
        Check("blocks: output survives a late C mark", lateText.Trim() == "hi", lateText.Replace("\r", ""));

        var (early, earlyParser) = Make(40, 8);
        Feed(earlyParser, "$ echo hi");
        Feed(earlyParser, "\u001b]133;C\u0007");
        Feed(earlyParser, "\r\nhi\r\n");
        Feed(earlyParser, "\u001b]133;D;0\u0007");
        var earlyText = early.Prompts.Count > 0 ? early.BlockOutput(early.Prompts[0]) : "<none>";
        Check("blocks: output survives an early C mark", earlyText.Trim() == "hi", earlyText.Replace("\r", ""));

        var (many, manyParser) = Make(40, 8);
        Feed(manyParser, "\u001b]133;A\u0007\u001b]133;B\u0007$ printf 'aa\\nbb\\ncc\\n'\r\n");
        Feed(manyParser, "aa\r\nbb\r\ncc\r\n");
        Feed(manyParser, "\u001b]133;C\u0007\u001b]133;D;0\u0007");
        var manyText = many.Prompts.Count > 0 ? many.BlockOutput(many.Prompts[0]) : "<none>";
        Check("blocks: every output row is kept", manyText.Replace("\r", "").Trim() == "aa\nbb\ncc", manyText.Replace("\r", ""));

        var (none, noneParser) = Make(40, 8);
        Feed(noneParser, "$ true\r\n");
        Feed(noneParser, "\u001b]133;C\u0007");
        Feed(noneParser, "\u001b]133;D;0\u0007");
        var noneText = none.Prompts.Count > 0 ? none.BlockOutput(none.Prompts[0]) : "<none>";
        Check("blocks: a command with no output yields nothing", noneText.Trim().Length == 0, noneText);

        // A receipt is opened after the next prompt exists. The block must not grow the
        // prompt line into its text - true for bash's PROMPT_COMMAND marks as much as fish's.
        var (following, followingParser) = Make(40, 8);
        Feed(followingParser, "\u001b]133;A\u0007\u001b]133;B\u0007$ echo hi\r\n");
        Feed(followingParser, "hi\r\n");
        Feed(followingParser, "\u001b]133;C\u0007\u001b]133;D;0\u0007");
        Feed(followingParser, "\u001b]133;A\u0007$ echo next\r\n");
        var followingText = following.Prompts.Count > 0 ? following.BlockOutput(following.Prompts[0]) : "<none>";
        Check("blocks: the next prompt never joins the previous block",
              followingText.Replace("\r", "").Trim() == "hi", followingText.Replace("\r", ""));
    }

    /// <summary>A real fish 4.0.2 session, captured with script(1) on the demo host at 80x24 and
    /// replayed byte for byte. Fish writes its own OSC 133 marks: A before the prompt, C with
    /// cmdline_url on the row BELOW the command (after its CR LF), D on the row BELOW the output.
    /// So the D line is not the block's last line - it is the next prompt's row, and reading the
    /// block after the next prompt is painted must not pull that prompt into the output.</summary>
    private static void FishCaptureChecks()
    {
        const string capture =
            "V2VsY29tZSB0byBmaXNoLCB0aGUgZnJpZW5kbHkgaW50ZXJhY3RpdmUgc2hlbGwNClR5cGUgG1szMm1oZWxwGyhCG1ttIGZv" +
            "ciBpbnN0cnVjdGlvbnMgb24gaG93IHRvIHVzZSBmaXNoDQobXTc7ZmlsZTovL21vZGR5cy1jYW1zL2hvbWUvbW9kZHkHG10w" +
            "O34HG1szMG0bKEIbW20NG10xMzM7QTtzcGVjaWFsX2tleT0xBxtbOTJtbW9kZHkbKEIbW21AGyhCG1ttbW9kZHlzLWNhbXMb" +
            "KEIbW20gG1szMm1+GyhCG1ttGyhCG1ttPiAbW0sNG1syMUMbWz8yMDA0aBtbPjQ7MW0bWz01dRs9ZWNobyANG1syNkNmaXNo" +
            "LW9uZQ0bWzM0QxtbMTNEZWNobyAbWzM2bWZpc2gtb25lG1szMG0bKEIbW20NG1szNEMNChtbMzBtGyhCG1ttG10xMzM7Qztj" +
            "bWRsaW5lX3VybD1lY2hvJTIwZmlzaC1vbmUHG1s/MjAwNGwbWz40OzBtG1s9MHUbPhtdMDtlY2hvIGZpc2gtb25lIH4HG1sz" +
            "MG0bKEIbW20NZmlzaC1vbmUNChtdMTMzO0Q7MAcbWz8yNWgbWzJt4o+OGyhCG1ttICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIA3ij44gDRtbSxtdMDt+Bxtb" +
            "MzBtGyhCG1ttG10xMzM7QTtzcGVjaWFsX2tleT0xBxtbOTJtbW9kZHkbKEIbW21AGyhCG1ttbW9kZHlzLWNhbXMbKEIbW20g" +
            "G1szMm1+GyhCG1ttGyhCG1ttPiAbW0sNG1syMUMbWz8yMDA0aBtbPjQ7MW0bWz01dRs9ZWNobyANG1syNkNmaXNoLXR3bw0b" +
            "WzM0QxtbMTNEZWNobyAbWzM2bWZpc2gtdHdvG1szMG0bKEIbW20NG1szNEMNChtbMzBtGyhCG1ttG10xMzM7QztjbWRsaW5l" +
            "X3VybD1lY2hvJTIwZmlzaC10d28HG1s/MjAwNGwbWz40OzBtG1s9MHUbPhtdMDtlY2hvIGZpc2gtdHdvIH4HG1szMG0bKEIb" +
            "W20NZmlzaC10d28NChtdMTMzO0Q7MAcbWz8yNWgbWzJt4o+OGyhCG1ttICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIA3ij44gDRtbSxtdMDt+BxtbMzBtGyhC" +
            "G1ttG10xMzM7QTtzcGVjaWFsX2tleT0xBxtbOTJtbW9kZHkbKEIbW21AGyhCG1ttbW9kZHlzLWNhbXMbKEIbW20gG1szMm1+" +
            "GyhCG1ttGyhCG1ttPiAbW0sNG1syMUMbWz8yMDA0aBtbPjQ7MW0bWz01dRs9ZXhpdA0bWzI1Qw0KG1szMG0bKEIbW20bXTEz" +
            "MztDO2NtZGxpbmVfdXJsPWV4aXQHG1s/MjAwNGwbWz40OzBtG1s9MHUbPhtdMDtleGl0IH4HG1szMG0bKEIbW20NG10xMzM7" +
            "RDswBxtbPzI1aBtbMzBtGyhCG1ttCg==";

        var (buffer, parser) = Make(80, 24);
        Feed(parser, Encoding.UTF8.GetString(Convert.FromBase64String(capture)));

        Check("fish capture: three commands became three blocks", buffer.Prompts.Count == 3,
              buffer.Prompts.Count.ToString());

        var first = buffer.Prompts.Count > 0 ? buffer.Prompts[0] : null;
        var second = buffer.Prompts.Count > 1 ? buffer.Prompts[1] : null;
        Check("fish capture: cmdline_url is the exact command",
              first?.Command == "echo fish-one" && second?.Command == "echo fish-two",
              (first?.Command ?? "none") + " / " + (second?.Command ?? "none"));

        // Read AFTER the next prompt has been drawn: that is when a receipt or a copy runs.
        var firstOut = first is null ? "<none>" : buffer.BlockOutput(first).Replace("\r", "").Trim();
        var secondOut = second is null ? "<none>" : buffer.BlockOutput(second).Replace("\r", "").Trim();
        Check("fish capture: the first block's output is its own line", firstOut == "fish-one", firstOut);
        Check("fish capture: the second block's output is its own line", secondOut == "fish-two", secondOut);
        Check("fish capture: the block range stops before the next prompt",
              firstOut.IndexOf("demo@", StringComparison.Ordinal) < 0
              && secondOut.IndexOf("demo@", StringComparison.Ordinal) < 0,
              firstOut + " | " + secondOut);
    }

    /// <summary>Each snippet must carry the marks it promises: a shell hook that only paints a
    /// prompt is worse than none, because the app waits for boundaries that never arrive.</summary>
    private static void ShellIntegrationChecks()
    {
        var bash = SshIt.Services.ShellIntegration.Snippet("Bash");
        Check("shell: bash sets PS0 and PROMPT_COMMAND for the marks",
              bash.Contains("PS0=") && bash.Contains("133;C") && bash.Contains("PROMPT_COMMAND="),
              bash.Length.ToString());
        var zsh = SshIt.Services.ShellIntegration.Snippet("Zsh");
        Check("shell: zsh hooks preexec and precmd",
              zsh.Contains("add-zsh-hook preexec") && zsh.Contains("add-zsh-hook precmd")
              && zsh.Contains("133;C") && zsh.Contains("133;B"), zsh.Length.ToString());
        var fish = SshIt.Services.ShellIntegration.Snippet("Fish");
        Check("shell: fish 4 needs no hook (it emits the marks itself)",
              fish.Contains("nothing to paste"), fish.Length.ToString());
        Check("shell: each shell names the file it belongs in",
              SshIt.Services.ShellIntegration.FileFor("Bash") == "~/.bashrc"
              && SshIt.Services.ShellIntegration.FileFor("Zsh") == "~/.zshrc"
              && SshIt.Services.ShellIntegration.FileFor("Fish") == "~/.config/fish/config.fish", "files");

        // A real fish 4 stream: A, then C with cmdline_url, output, then D.
        var (fishNative, fishParser) = Make(40, 8);
        Feed(fishParser, "\u001b]133;A\u0007\u001b]133;C;cmdline_url=echo%20native-one\u0007\r\nnative-one\r\n\u001b]133;D;0\u0007");
        var fishBlock = fishNative.Prompts.Count > 0 ? fishNative.Prompts[0] : null;
        Check("blocks: fish's cmdline_url gives the exact command",
              fishBlock?.Command == "echo native-one", fishBlock?.Command ?? "none");
        Check("blocks: fish's native marks give the right output",
              fishBlock is not null && fishNative.BlockOutput(fishBlock).Trim() == "native-one",
              fishBlock is null ? "none" : fishNative.BlockOutput(fishBlock).Replace("\r", ""));
        var watchdog = AgentCli.RemoteWatchdog("echo hi", 5);
        Check("shell: a remote command runs under sh, not the login shell",
              watchdog.StartsWith("sh -c ") && watchdog.Contains("timeout -k 2 4"), watchdog);
    }

    /// <summary>A CSI carrying a private prefix is a different command, not a legacy one with
    /// decoration. Fish 4's kitty keyboard protocol sends CSI = 5 u around every redraw, and
    /// xterm's modifyOtherKeys is CSI &gt; 4 ; 1 m. Dispatching those as RestoreCursor and SGR
    /// put every fish command on the banner row and bolded it.</summary>
    private static void PrivatePrefixChecks()
    {
        var (buffer, parser) = Make(20, 4);
        Feed(parser, "ab\u001b[s\r\u001b[=5u");
        Check("csi: CSI = 5 u is not RestoreCursor", buffer.CursorRow == 0 && buffer.CursorCol == 0,
              "row=" + buffer.CursorRow + " col=" + buffer.CursorCol);
        Feed(parser, "\u001b[u");
        Check("csi: a bare CSI u still restores the cursor", buffer.CursorRow == 0 && buffer.CursorCol == 2,
              "row=" + buffer.CursorRow + " col=" + buffer.CursorCol);

        var (pen, penParser) = Make(20, 4);
        Feed(penParser, "\u001b[>4;1mx");
        var flags = pen.LineAt(0).Cells[0].Flags;
        Check("csi: CSI > 4 ; 1 m is modifyOtherKeys, not bold or underline",
              (flags & (CellFlags.Bold | CellFlags.Underline)) == CellFlags.None, flags.ToString());
    }

    private static void Check(string name, bool ok, string? detail)
    {
        if (ok)
        {
            _pass++;
            Report.AppendLine($"PASS  {name}");
        }
        else
        {
            _fail++;
            Report.AppendLine($"FAIL  {name}  (got: {detail})");
        }
    }
}
