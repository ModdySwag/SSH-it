using System.Diagnostics;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Reads the remote tmux version before a control-mode attach. The window drives tmux through
/// `%pause` / `refresh-client -A` and a capture resync, which arrived in tmux 3.0; an older server
/// never sends `%pause`, and — measured against a source build of 2.9 — does not answer the attach
/// query at all, so the window would sit empty until its query timeout. A version line is a better
/// answer than a timeout, and it is the only honest way to declare a floor.
/// </summary>
public static class TmuxVersion
{
    /// <summary>The oldest server this client claims to drive.</summary>
    public const int MinimumMajor = 3;
    public const int MinimumMinor = 0;
    public const string Minimum = "3.0";

    private static readonly Regex VersionLine = new(@"tmux\s+(\d+)\.(\d+)", RegexOptions.IgnoreCase);

    /// <summary>"tmux 3.5a" -> (3, 5); "tmux 2.9" -> (2, 9); anything else -> null (unknown, never a refusal).</summary>
    public static (int Major, int Minor)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = VersionLine.Match(text);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var major)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var minor)) return null;
        return (major, minor);
    }

    public static bool IsSupported((int Major, int Minor) version)
        => version.Major > MinimumMajor || (version.Major == MinimumMajor && version.Minor >= MinimumMinor);

    /// <summary>The sentence the window shows instead of attaching to an old server.</summary>
    public static string Refusal((int Major, int Minor) version, string host)
        => $"tmux {version.Major}.{version.Minor} on {host} is older than this window can drive.\n\n"
           + $"It needs tmux {Minimum} or newer: `%pause` and `refresh-client -A` arrived in 3.0, and a "
           + "2.9 server does not answer the attach query at all.\n\n"
           + "Upgrade tmux on that host, or use a plain SSH session to it. Nothing else in SSH-it is affected.";

    /// <summary>
    /// Runs `tmux -V` over the same transport the window would use. Returns null when the probe cannot
    /// run or the output is not a version line — the attach path then reports its own error, so an
    /// unknown server is never blocked by a guess.
    /// </summary>
    public static async Task<(int Major, int Minor)?> ProbeAsync(
        Session? sshSession, string? localDistro, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (sshSession is null)
        {
            psi.FileName = "wsl.exe";
            if (!string.IsNullOrWhiteSpace(localDistro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(localDistro!); }
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tmux");
            psi.ArgumentList.Add("-V");
        }
        else
        {
            psi.FileName = SshLauncher.SshExe;
            psi.Arguments = SshLauncher.BuildCommandArguments(sshSession, "tmux -V");
            foreach (var pair in SshLauncher.BuildEnvironment(sshSession, null)) psi.Environment[pair.Key] = pair.Value;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                Log.Warn("tmux version probe timed out after " + timeoutMs + " ms");
                return null;
            }
            return Parse(await read);
        }
        catch (Exception ex)
        {
            Log.Warn("tmux version probe failed: " + ex.Message);
            return null;
        }
    }
}
