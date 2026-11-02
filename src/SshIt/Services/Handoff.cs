using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// How a second launch of SSH-it talks to the instance that already holds the single-instance
/// mutex. A named pipe, not an event: the message has to say what was asked for
/// (--connect &lt;id&gt;, --new, --options, a new window) and an event cannot carry that.
/// The pipe is current-user only and reaches saved sessions at most, never a credential.
/// </summary>
public static class Handoff
{
    public const string PipeName = "SSH-it-handoff";

    public const string Focus = "focus";
    public const string NewWindow = "new-window";
    public const string NewSession = "new-session";
    public const string Options = "options";
    public const string Connect = "connect";

    /// <summary>The request this process should send, worked out from its command line.</summary>
    public static string ForArgs(IReadOnlyList<string> args)
    {
        var index = IndexOf(args, "--connect");
        if (index >= 0 && index + 1 < args.Count) return Connect + " " + args[index + 1];
        if (IndexOf(args, "--new") >= 0) return NewSession;
        if (IndexOf(args, "--options") >= 0) return Options;
        if (IndexOf(args, "--newwindow") >= 0) return NewWindow;
        return Focus;
    }

    /// <summary>Splits "connect abc123" into its verb and argument.</summary>
    public static (string Verb, string Argument) Parse(string? message)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0) return (Focus, "");

        var space = text.IndexOf(' ');
        if (space < 0) return (text.ToLowerInvariant(), "");
        return (text[..space].ToLowerInvariant(), text[(space + 1)..].Trim());
    }

    /// <summary>
    /// Sends one request to the running instance. The listening thread is started just after
    /// the mutex is taken, so a launch that races it retries for a few seconds instead of
    /// silently doing nothing.
    /// </summary>
    public static bool Send(string message)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(400);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(message);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(125);
            }
        }
        return false;
    }

    private static int IndexOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
