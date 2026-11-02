using System.Text;

namespace SshIt.Services;

/// <summary>One pane a fan-out could reach, and whether it will.</summary>
public sealed class FanoutTarget
{
    public string Tab { get; init; } = "";
    public string Session { get; init; } = "";
    public bool Connected { get; init; }
    public bool Locked { get; init; }

    /// <summary>"pane 2 of 3" when a tab holds more than one; blank for a single-pane tab.</summary>
    public string Pane { get; init; } = "";

    /// <summary>Locked first: a lock is deliberate, "not connected" is usually just a gone host.</summary>
    public bool WillReceive => Connected && !Locked;

    public string State => Locked ? "locked" : Connected ? "connected" : "not connected";

    public string Label
    {
        get
        {
            var label = Tab.Length > 0 && Tab != Session ? Tab + " — " + Session : Session;
            return Pane.Length > 0 ? label + " · " + Pane : label;
        }
    }
}

/// <summary>
/// Send one command to every connected pane, across tabs. The risky half is knowing WHERE it goes, so
/// the plan is built and described before anything is written, and the sentence is testable without a UI.
/// </summary>
public static class Fanout
{
    /// <summary>Every pane that would receive the command, in tab order.</summary>
    public static int Receivers(IEnumerable<FanoutTarget> plan) => plan.Count(target => target.WillReceive);

    /// <summary>The dialog's Send button: a receiver exists AND the user typed something.</summary>
    public static bool CanSend(IEnumerable<FanoutTarget> plan, string? command)
        => !string.IsNullOrWhiteSpace(command) && Receivers(plan) > 0;

    /// <summary>
    /// "3 panes will receive it; 2 are skipped (1 not connected, 1 locked)." A fan-out to nothing says so
    /// instead of reporting success.
    /// </summary>
    public static string Summary(IReadOnlyCollection<FanoutTarget> plan)
    {
        var receivers = Receivers(plan);
        if (receivers == 0)
            return plan.Count == 0
                ? "No open panes."
                : "Nothing to send — no pane is connected and unlocked.";

        var text = receivers == 1 ? "1 pane will receive it" : receivers + " panes will receive it";
        var skipped = plan.Count - receivers;
        if (skipped == 0) return text + ".";

        var locked = plan.Count(target => target.Locked);
        var offline = plan.Count(target => !target.Connected && !target.Locked);
        var parts = new List<string>();
        if (offline > 0) parts.Add(offline == 1 ? "1 not connected" : offline + " not connected");
        if (locked > 0) parts.Add(locked == 1 ? "1 locked" : locked + " locked");
        return text + "; " + skipped + " skipped (" + string.Join(", ", parts) + ").";
    }

    /// <summary>
    /// The bytes one pane gets: the command with exactly one carriage return, so a pasted multi-line
    /// block cannot run several commands per pane by accident.
    /// </summary>
    public static byte[] CommandBytes(string command)
    {
        var text = (command ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        return Encoding.UTF8.GetBytes(text + "\r");
    }
}
