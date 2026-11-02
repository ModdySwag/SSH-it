namespace SshIt.Models;

/// <summary>Application-wide preferences, persisted to settings.json.</summary>
public sealed class AppSettings
{
    // Appearance
    public string Theme { get; set; } = "System";          // System | Dark | Light
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 13.0;
    public string CursorStyle { get; set; } = "Block";     // Block | Bar | Underline
    public bool CursorBlink { get; set; } = true;
    public int ScrollbackLines { get; set; } = 10000;
    public bool UseLigatures { get; set; } = false;
    public string TerminalTheme { get; set; } = "SSH-it Dark";

    /// <summary>Keyword-highlighting rules, applied in order; the first rule to claim a character owns it.</summary>
    public List<HighlightRule> HighlightRules { get; set; } = new();

    // Behaviour
    public bool CopyOnSelect { get; set; } = false;
    public bool ConfirmOnClose { get; set; } = true;
    public bool CloseToTray { get; set; } = false;
    public bool MinimizeToTray { get; set; } = false;
    public bool ReopenTabs { get; set; } = true;

    /// <summary>The Files panel follows the shell's working directory (OSC 7) unless turned off.</summary>
    public bool SftpFollowShell { get; set; } = true;

    /// <summary>Resume a download from the end of the local file instead of starting it again.</summary>
    public bool SftpResume { get; set; } = false;

    /// <summary>The local folder the local half of the Files panel was last showing.</summary>
    public string SftpLocalPath { get; set; } = "";
    public bool MonitorByDefault { get; set; } = true;
    public bool LogSessions { get; set; } = false;
    public bool RecordSessions { get; set; } = false;
    public bool BellSound { get; set; } = false;
    public bool WarnOnMultiPaste { get; set; } = true;

    // ZMODEM (sz/rz) inside the terminal stream. On by default: it only engages when a
    // peer sends a ZRQINIT header, and a received file always lands in the download
    // folder under its leaf name.
    public bool Zmodem { get; set; } = true;
    /// <summary>Where ZMODEM downloads land. Empty means %USERPROFILE%\Downloads.</summary>
    public string ZmodemDownloadFolder { get; set; } = "";

    // Agent access
    /// <summary>When on, the headless agent verbs may only use sessions that allow them.</summary>
    public bool AgentGate { get; set; } = false;

    // Layout
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 268;
    public bool RightPanelVisible { get; set; } = true;
    public double RightPanelWidth { get; set; } = 390;
    public bool StatusBarVisible { get; set; } = true;

    // Window placement
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; } = false;

    /// <summary>Window opacity, 1.0 = solid. Ctrl+scroll lowers it so text behind the window can be read.</summary>
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>Window stays at the bottom of the z-order so other apps stay in front of it.</summary>
    public bool PinToDesktop { get; set; }

    // State
    public string? LastSelectedSessionId { get; set; }
    public List<string> OpenTabSessionIds { get; set; } = new();
    public List<string> RecentSessionIds { get; set; } = new();

    /// <summary>
    /// Extra main windows, each with its own placement and tab set. The first window keeps
    /// using the legacy WindowLeft/Top/Width/Height and OpenTabSessionIds fields above, so a
    /// settings file written before multi-window still restores one window exactly as it was.
    /// </summary>
    public List<WindowLayout> ExtraWindows { get; set; } = new();

    public void AddRecent(string id)
    {
        RecentSessionIds.Remove(id);
        RecentSessionIds.Insert(0, id);
        if (RecentSessionIds.Count > 12)
            RecentSessionIds.RemoveRange(12, RecentSessionIds.Count - 12);
    }
}
