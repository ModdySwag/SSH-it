using System.Text;
using SshIt.Terminal;

namespace SshIt.Services;

/// <summary>
/// A tmux pane as data: the control-mode stream on one side, a <see cref="TerminalBuffer"/> and the
/// commands a UI needs on the other. Control mode is not a VT stream — `%output` carries the pane's
/// bytes already escaped — so the bridge decodes the protocol and feeds the same buffer the SSH panes
/// use. A pane control only has to draw the buffer and call <see cref="KeySequence"/>.
/// </summary>
public sealed class TmuxPaneBridge
{
    private readonly TerminalBuffer _buffer;
    private readonly VtParser _parser;
    private readonly TmuxControlParser _control = new();

    public TmuxPaneBridge(int paneId, int columns = 80, int rows = 24)
        : this(new TerminalBuffer(columns, rows), paneId)
    {
    }

    /// <summary>
    /// Wraps a buffer the host already owns, so a WPF pane can keep one <see cref="TerminalBuffer"/>
    /// for the view, the ledger and the bridge instead of drawing a second copy of the screen.
    /// </summary>
    public TmuxPaneBridge(TerminalBuffer buffer, int paneId)
    {
        PaneId = paneId;
        _buffer = buffer;
        _parser = new VtParser(_buffer, _ => { });
    }

    /// <summary>The tmux pane id (%N). A negative id accepts output from any pane.</summary>
    public int PaneId { get; }

    public TerminalBuffer Buffer => _buffer;

    /// <summary>The last layout the server reported, when it sent one.</summary>
    public TmuxLayout? CurrentLayout { get; private set; }

    public bool Exited { get; private set; }

    /// <summary>Feeds one line of the control stream; returns the event, if the line was protocol.</summary>
    public TmuxEvent? FeedLine(string line)
    {
        var produced = _control.FeedLine(line);
        if (produced is not null) Apply(produced);
        return produced;
    }

    public void Apply(TmuxEvent produced)
    {
        switch (produced.Kind)
        {
            case TmuxEventKind.Output when PaneId < 0 || produced.PaneId == PaneId:
                _parser.Feed(Encoding.UTF8.GetBytes(produced.Text));
                break;

            case TmuxEventKind.LayoutChange:
                try { CurrentLayout = TmuxLayout.Parse(produced.Text); }
                catch (FormatException) { /* a layout we cannot read is not a reason to drop the pane */ }
                break;

            case TmuxEventKind.Exit:
                Exited = true;
                break;
        }
    }

    /// <summary>Keystrokes go to the server as bytes; the shell never sees a half-character.</summary>
    public string KeySequence(string text) => TmuxControlParser.SendKeysHex(PaneId, text);

    public string ResizeSequence(int columns, int rows) => TmuxControlParser.ResizeClient(columns, rows);

    /// <summary>After `%pause`, ask for the pane's visible text (`capture-pane -p -e`) and feed it back.</summary>
    public string ResyncSequence() => TmuxControlParser.CapturePane(PaneId);

    /// <summary>`refresh-client -A pane:state`; the client resumes a paused pane with this.</summary>
    public static string ResumeSequence(int paneId) => "refresh-client -A \"%" + paneId + ":continue\"";

    /// <summary>
    /// A marker command sent right after a capture. Its answer is the only reliable way to know
    /// which `%end` carried the capture: a send-keys answer can land between the two, so "the next
    /// empty response" is not the capture.
    /// </summary>
    public string ResyncMarker() => "display-message -p 'SSHIT-RESYNC-" + PaneId + "-END'";

    /// <summary>
    /// Replaces the visible screen with a `capture-pane -p -e` response. Scrollback is kept: a pause
    /// is a stale screen, not a new session. The capture separates lines with LF; a terminal needs
    /// CRLF or every line restarts in the column the previous one ended in.
    /// </summary>
    public void ApplyCapture(string text)
    {
        _buffer.EraseInDisplay(2);
        _buffer.SetCursor(0, 0);
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        if (normalised.Length > 0) _parser.Feed(Encoding.UTF8.GetBytes(normalised));
    }

    /// <summary>
    /// On `%pause` the server stopped sending output and the client's screen is stale: ask for the
    /// visible text (`capture-pane -p -e`), then a marker that proves the capture has arrived. The
    /// caller resumes the pane with <see cref="ResumeSequence"/> only after the marker.
    /// </summary>
    public IReadOnlyList<string> PauseRecovery() => new[] { ResyncSequence(), ResyncMarker() };
}
