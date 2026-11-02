using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using SshIt.Terminal;

namespace SshIt.Services;

/// <summary>
/// R3-24: what a screen reader hears. The terminal paints its own glyphs, so a command's result is
/// invisible to accessibility tools unless the app says it out loud: a TextBlock marked as a polite
/// UIA live region whose automation peer raises LiveRegionChanged whenever its text changes.
///
/// The wording lives here rather than in the window so it can be tested without a UI thread, and so
/// every announcement reads the same way. "Command finished" is deliberate: a reader hears the
/// result, not the fact that some pixels changed.
/// </summary>
public static class Announcer
{
    /// <summary>The id a UIA client (or a probe) uses to find the live region.</summary>
    public const string RegionAutomationId = "AnnounceRegion";

    /// <summary>The longest command quoted in an announcement; a 400-character one-liner is noise.</summary>
    public const int MaxCommandLength = 48;

    public static string StateName(SessionState state) => state switch
    {
        SessionState.Connecting => "connecting",
        SessionState.Connected => "connected",
        SessionState.Disconnected => "disconnected",
        SessionState.Failed => "failed",
        _ => "not connected"
    };

    /// <summary>
    /// "Command finished — "make", exit 0, 1.2 seconds." A background tab's completion names its
    /// session first, so the reader knows which tab the line belongs to.
    /// </summary>
    public static string BlockAnnouncement(string? command, int exit, TimeSpan duration, string? session = null)
    {
        var text = "Command finished";
        var name = OneLine(command);
        if (name.Length > MaxCommandLength) name = name[..(MaxCommandLength - 1)] + "…";
        if (name.Length > 0) text += " — \"" + name + "\"";
        text += ", exit " + exit.ToString(CultureInfo.InvariantCulture) + ", " + Duration(duration) + ".";

        if (!string.IsNullOrWhiteSpace(session)) text = session!.Trim() + ": " + text;
        return text;
    }

    /// <summary>Connection state as a sentence, used when a pane connects or fails.</summary>
    public static string StateAnnouncement(string session, SessionState state, string? error = null)
    {
        var name = string.IsNullOrWhiteSpace(session) ? "Session" : session.Trim();
        var text = name + ": " + StateName(state) + ".";
        if (state == SessionState.Failed && !string.IsNullOrWhiteSpace(error))
        {
            var detail = OneLine(error);
            if (detail.Length > 120) detail = detail[..119] + "…";
            text = name + ": connection failed — " + detail;
        }
        return text;
    }

    /// <summary>
    /// Seconds under a minute, "m min s" above it. A duration is always shown with one decimal so
    /// two announcements read differently instead of both saying "1 second".
    /// </summary>
    public static string Duration(TimeSpan duration)
    {
        var seconds = duration.TotalSeconds;
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;

        if (seconds < 60)
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " seconds";

        var minutes = (int)(seconds / 60);
        var rest = (int)Math.Round(seconds - (minutes * 60));
        if (rest == 60)
        {
            minutes++;
            rest = 0;
        }
        return minutes.ToString(CultureInfo.InvariantCulture) + " min "
               + rest.ToString(CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>Marks a TextBlock as the app's live region. Call once, when the window is built.</summary>
    public static void Enable(TextBlock region)
    {
        AutomationProperties.SetAutomationId(region, RegionAutomationId);
        AutomationProperties.SetLiveSetting(region, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(region, "");
    }

    /// <summary>
    /// Sets the text and raises LiveRegionChanged. The Name is updated too: a client that reads the
    /// element's name (rather than its text pattern) then hears the same sentence.
    /// </summary>
    public static void Announce(TextBlock region, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        region.Text = text;
        AutomationProperties.SetName(region, text);

        var peer = UIElementAutomationPeer.FromElement(region) ?? UIElementAutomationPeer.CreatePeerForElement(region);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var single = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        while (single.Contains("  ", StringComparison.Ordinal)) single = single.Replace("  ", " ");
        return single.Trim();
    }
}
