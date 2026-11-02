namespace SshIt.Services;

/// <summary>
/// When a session drops unexpectedly, how long to wait before trying again. The ladder is short and
/// finite on purpose: a client that retries forever hides a broken host behind a spinner, and one that
/// retries immediately turns a refusing server into a connection storm.
///
/// A clean exit (the shell ended normally) is never retried, a user's Disconnect stops the ladder at
/// once, and a manual Connect resets it.
/// </summary>
public static class ReconnectPolicy
{
    /// <summary>Seconds to wait before attempt 1..5. After the last one the client stops asking.</summary>
    public static readonly int[] Delays = { 2, 5, 15, 30, 60 };

    public static int? NextDelay(int attempt, bool userStopped, bool autoReconnect)
    {
        if (!autoReconnect || userStopped) return null;
        if (attempt < 0 || attempt >= Delays.Length) return null;
        return Delays[attempt];
    }

    public static string Message(int seconds) =>
        "Reconnecting in " + seconds + " s — Session > Disconnect stops this.";
}
