using SshIt.Models;

namespace SshIt.Services;

/// <summary>Thrown when the agent gate refuses a session.</summary>
public sealed class AgentAccessDeniedException : Exception
{
    public AgentAccessDeniedException(string message) : base(message) { }
}

/// <summary>
/// Decides whether the headless agent surface may use a session.
///
/// The gate is opt-in: with `AgentGate` off, every verb behaves exactly as it always has.
/// With it on, only sessions whose "Allow agent commands" box is ticked can be used — an
/// allowlist, so switching the gate on locks the surface down until hosts are named on
/// purpose, and turning it off again restores the old behaviour without losing the marks.
/// </summary>
public static class AgentPolicy
{
    public static bool Allows(Session session, AppSettings settings)
        => !settings.AgentGate || session.AllowAgent;

    public static string RefusalMessage(Session session)
        => $"agent access is off for '{session.DisplayTarget}'. Tick 'Allow agent commands' in the session editor, or turn off the agent gate in Settings.";

    /// <summary>Throws when the session may not be used by an agent verb.</summary>
    public static void Require(Session session, AppSettings settings)
    {
        if (!Allows(session, settings)) throw new AgentAccessDeniedException(RefusalMessage(session));
    }
}
