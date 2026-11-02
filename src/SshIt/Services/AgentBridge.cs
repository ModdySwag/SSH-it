using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Terminal;

namespace SshIt.Services;

/// <summary>
/// The bridge the MCP server uses to SEE the panes that live in the GUI process. The MCP server is a
/// separate process (`--mcp` stdio), so observation cannot read another process's memory: a per-user
/// named pipe carries one JSON request and one JSON reply.
///
/// Trust boundary: the pipe is current-user only (PipeOptions.CurrentUserOnly on both ends);
/// observation is read-only; typing runs through the same lease/policy gate as `--exec`, so an agent
/// that may not run a command may not type one either.
/// </summary>
public static class AgentBridge
{
    public const string PipeName = "SSH-it-agent";

    private static readonly UTF8Encoding Utf8 = new(false);

    // ---------------------------------------------------------------- GUI side

    /// <summary>Starts the listener once, on a background thread, after the first window exists.</summary>
    public static void Start()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "agent-bridge" };
        thread.Start();
        Log.Info("agent bridge listening on " + PipeName);
    }

    private static void Loop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Utf8);
                using var writer = new StreamWriter(server, Utf8) { AutoFlush = true };
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var reply = Answer(line);
                writer.WriteLine(reply);
                writer.Flush();
                // Hold the connection until the client has read the reply and closed its end.
                // Disposing first loses the bytes: the caller then sees "Cannot access a closed pipe"
                // even though the request ran (observed with a real MCP client).
                var hold = Task.Run(() => { try { reader.ReadLine(); } catch { } });
                hold.Wait(5000);
            }
            catch (Exception ex)
            {
                Log.Warn("agent bridge: " + ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    private static string Answer(string line)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject
                      ?? throw new InvalidOperationException("the request is not a JSON object");
        }
        catch (Exception ex) { return Error("the request is not a JSON object: " + ex.Message).ToJsonString(); }

        try
        {
            Log.Info("agent bridge: op=" + Text(request, "op"));
            return Handle(request).ToJsonString();
        }
        catch (Exception ex) { return Error(ex.Message).ToJsonString(); }
    }

    private static JsonObject Handle(JsonObject request) => Text(request, "op") switch
    {
        "list" => OnUi(List),
        "observe" => OnUi(() => Observe(request)),
        "blocks" => OnUi(() => Blocks(request)),
        "wait" => Wait(request),
        "type" => OnUi(() => Type(request)),
        var op => Error("unknown op '" + op + "' (list, observe, blocks, wait, type)")
    };

    private static T OnUi<T>(Func<T> work)
    {
        var app = Application.Current;
        if (app is null) throw new InvalidOperationException("the GUI is shutting down");
        return app.Dispatcher.Invoke(work, DispatcherPriority.Normal);
    }

    // ---------------------------------------------------------------- operations

    private static JsonObject List()
    {
        var array = new JsonArray();
        foreach (var pane in Panes()) array.Add(State(pane));
        return new JsonObject { ["ok"] = true, ["panes"] = array };
    }

    private static JsonObject Observe(JsonObject request)
    {
        var pane = FindPane(Text(request, "session"));
        if (pane is null) return Error("no open pane matches '" + Text(request, "session") + "'");
        var refusal = ReadRefusal(pane);
        if (refusal is not null) return Blocked(refusal);
        var ttl = Math.Clamp(Long(request, "ttlSeconds", 60), 5, 3600);
        var redact = Patterns(request);
        var recent = new JsonArray();
        foreach (var mark in pane.Buffer.Prompts.TakeLast(5)) recent.Add(Block(pane, mark, redact));
        var state = State(pane);
        state["ttlSeconds"] = ttl;
        state["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(ttl).ToString("O");
        state["observer"] = Register(pane.Session.Id, (int)ttl, redact);
        state["recent"] = recent;
        return new JsonObject { ["ok"] = true, ["pane"] = state };
    }

    private static JsonObject Blocks(JsonObject request)
    {
        var pane = FindPane(Text(request, "session"));
        if (pane is null) return Error("no open pane matches '" + Text(request, "session") + "'");
        var refusal = ReadRefusal(pane);
        if (refusal is not null) return Blocked(refusal);
        var expired = ObserverRefusal(request, out var redact);
        if (expired is not null) return expired;
        var since = Long(request, "since", 0);
        var limit = (int)Math.Clamp(Long(request, "limit", 20), 1, 200);
        var array = new JsonArray();
        foreach (var mark in pane.Buffer.Prompts.Where(mark => mark.Seq > since).TakeLast(limit))
            array.Add(Block(pane, mark, redact));
        return new JsonObject
        {
            ["ok"] = true, ["session"] = pane.Session.Id, ["blocks"] = array,
            ["lastSeq"] = pane.Buffer.Prompts.Count > 0 ? pane.Buffer.Prompts[^1].Seq : 0
        };
    }

    /// <summary>
    /// Polls on the bridge thread; each read hops to the UI thread for a moment. A wait never blocks
    /// the dispatcher, which is why it is the only op that does not run entirely inside OnUi.
    /// </summary>
    private static JsonObject Wait(JsonObject request)
    {
        var key = Text(request, "session");
        var until = Text(request, "until", "prompt");
        var pattern = Text(request, "pattern");
        var timeout = (int)Math.Clamp(Long(request, "timeoutMs", 30000), 100, 300_000);
        var since = Long(request, "since", -1);
        var observerId = Text(request, "observer");
        var observer = Lookup(observerId);
        if (observerId.Length > 0 && observer is null)
            return Expired("the observation has expired; call session_observe again");
        if (observer is not null)
        {
            // A wait may not outlive the observation that asked for it.
            var remaining = (int)Math.Max(250, (observer.Expires - DateTime.UtcNow).TotalMilliseconds);
            timeout = Math.Min(timeout, remaining);
        }
        var watch = Stopwatch.StartNew();

        while (watch.ElapsedMilliseconds < timeout)
        {
            var check = OnUi(() => WaitCheck(key, until, pattern, since));
            if (check is null) return Error("no open pane matches '" + key + "'");
            if (check["sat"]!.GetValue<bool>())
            {
                check["ok"] = true;
                check["waitedMs"] = (int)watch.ElapsedMilliseconds;
                return check;
            }
            Thread.Sleep(100);
        }

        return observer is not null
            ? Expired("the observation expired before '" + until + "' was satisfied")
            : new JsonObject
            {
                ["ok"] = true, ["sat"] = false, ["reason"] = "timeout",
                ["waitedMs"] = (int)watch.ElapsedMilliseconds, ["until"] = until, ["session"] = key
            };
    }

    private static JsonObject? WaitCheck(string key, string until, string pattern, long since)
    {
        var pane = FindPane(key);
        if (pane is null) return null;
        var marks = pane.Buffer.Prompts;
        var last = marks.Count > 0 ? marks[^1] : null;
        var baseline = since >= 0 ? since : 0;
        var sat = until.ToLowerInvariant() switch
        {
            "prompt" => last is { EndLine: >= 0 },
            "exit" => marks.Any(mark => mark.Seq > baseline && mark.EndLine >= 0),
            "pattern" => pattern.Length > 0 && pane.Buffer.Tail(400).Contains(pattern, StringComparison.Ordinal),
            _ => false
        };
        return new JsonObject
        {
            ["sat"] = sat, ["until"] = until, ["state"] = Announcer.StateName(pane.State),
            ["lastSeq"] = last?.Seq ?? 0, ["blocks"] = marks.Count,
            ["lastCommand"] = last?.Command, ["lastExit"] = last?.ExitCode ?? -1
        };
    }

    private static JsonObject Type(JsonObject request)
    {
        var key = Text(request, "session");
        var text = Text(request, "text");
        if (text.Length == 0) return Error("text is required");
        var enter = Bool(request, "enter", true);
        var pane = FindPane(key);
        if (pane is null) return Error("no open pane matches '" + key + "'");
        if (pane.IsLocked) return Blocked("the pane is locked; unlock it in SSH-it first");
        if (pane.State != SessionState.Connected)
            return Blocked("the pane is " + Announcer.StateName(pane.State) + ", so nothing would receive the text");

        // The same gate --exec runs through: a policy or lease that would refuse the command refuses
        // the typing, and the bytes never reach the pty (the proof checks exactly that).
        try
        {
            var store = App.Instance?.Store;
            if (store is null) return Blocked("the session store is not loaded");
            AgentPolicy.Require(pane.Session, store.Settings);
            AgentPolicyFile.Require(pane.Session, "exec", text);
            AgentLease.Require(pane.Session, "exec", text);
        }
        catch (Exception ex)
        {
            // A machine caller needs to tell "a human must approve" from "this will never run".
            var pending = ex.Message.Contains("two-person token", StringComparison.OrdinalIgnoreCase);
            return new JsonObject
            {
                ["ok"] = false, ["blocked"] = true,
                ["approval"] = pending ? "pending" : "denied",
                ["error"] = ex.Message,
                ["session"] = pane.Session.Id, ["command"] = text
            };
        }

        var suffix = enter ? ((char)13).ToString() : "";
        var bytes = Encoding.UTF8.GetBytes(text + suffix);
        if (!pane.SendRaw(bytes)) return Blocked("the pane has no live child");
        Log.Info("agent bridge: typed " + text.Length + " char(s) into " + pane.Session.Id);
        return new JsonObject
        {
            ["ok"] = true, ["session"] = pane.Session.Id, ["typed"] = text.Length, ["enter"] = enter
        };
    }

    // ---------------------------------------------------------------- observations

    internal sealed class Observer
    {
        public required string SessionId { get; init; }
        public required DateTime Expires { get; init; }
        public required List<Regex> Redact { get; init; }
    }

    private static readonly Dictionary<string, Observer> Observers = new(StringComparer.Ordinal);
    private static readonly object ObserverGate = new();

    /// <summary>
    /// A short-lived observation handle. The roadmap's ttl is enforced here: once it expires, the
    /// handle cannot read blocks or wait any more, and the caller has to observe again.
    /// </summary>
    internal static string Register(string sessionId, int ttlSeconds, List<Regex> redact)
    {
        var id = Guid.NewGuid().ToString("n")[..12];
        lock (ObserverGate)
        {
            Prune();
            Observers[id] = new Observer
            {
                SessionId = sessionId,
                Expires = DateTime.UtcNow.AddSeconds(ttlSeconds),
                Redact = redact
            };
        }
        return id;
    }

    internal static Observer? Lookup(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (ObserverGate)
        {
            Prune();
            return Observers.TryGetValue(id, out var observer) ? observer : null;
        }
    }

    private static void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var key in Observers.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToList())
            Observers.Remove(key);
    }

    /// <summary>The one URI shape this server exposes, and the inverse a resource read needs.</summary>
    public static string BlocksUri(string sessionId)
        => "terminal://session/" + Uri.EscapeDataString(sessionId) + "/blocks";

    public static string? SessionFromUri(string? uri)
    {
        const string prefix = "terminal://session/";
        const string suffix = "/blocks";
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri[prefix.Length..];
        if (!rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = rest[..^suffix.Length];
        return id.Length == 0 ? null : Uri.UnescapeDataString(id);
    }

    private static JsonObject Expired(string message)
        => new() { ["ok"] = false, ["expired"] = true, ["error"] = message };

    private static JsonObject? ObserverRefusal(JsonObject request, out List<Regex> redact)
    {
        redact = Patterns(request);
        var id = Text(request, "observer");
        if (id.Length == 0) return null;
        var observer = Lookup(id);
        if (observer is null) return Expired("the observation has expired; call session_observe again");
        if (redact.Count == 0) redact = observer.Redact;
        return null;
    }

    // ---------------------------------------------------------------- frames

    private static JsonObject State(TerminalPane pane)
    {
        var last = pane.Buffer.Prompts.Count > 0 ? pane.Buffer.Prompts[^1] : null;
        return new JsonObject
        {
            ["id"] = pane.Session.Id,
            ["name"] = pane.Session.Name,
            ["target"] = pane.Session.DisplayTarget,
            ["host"] = pane.Session.Host,
            ["state"] = Announcer.StateName(pane.State),
            ["locked"] = pane.IsLocked,
            ["visible"] = pane.IsVisible,
            ["blocks"] = pane.Buffer.Prompts.Count,
            ["lastSeq"] = last?.Seq ?? 0,
            ["promptReady"] = last is { EndLine: >= 0 },
            ["lastCommand"] = last?.Command,
            ["lastExit"] = last?.ExitCode ?? -1
        };
    }

    private static JsonObject Block(TerminalPane pane, PromptMark mark, List<Regex> redact)
    {
        var output = Redacted(pane.Buffer.BlockOutput(mark) ?? "", redact);
        if (output.Length > 4000) output = output[..4000] + "\n… truncated …";
        return new JsonObject
        {
            ["seq"] = mark.Seq,
            ["command"] = Redacted(mark.Command ?? "", redact),
            ["output"] = output,
            ["exit"] = mark.ExitCode,
            ["started"] = mark.Started.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["durationMs"] = (int)mark.Duration.TotalMilliseconds,
            ["cwd"] = mark.Cwd
        };
    }

    private static List<Regex> Patterns(JsonObject request)
    {
        var patterns = new List<Regex>();
        if (request["redact"] is not JsonArray array) return patterns;
        foreach (var node in array)
        {
            var pattern = node?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try { patterns.Add(new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(250))); }
            catch { /* an invalid pattern is ignored, not fatal */ }
        }
        return patterns;
    }

    private static string Redacted(string text, List<Regex> patterns)
    {
        foreach (var pattern in patterns)
        {
            try { text = pattern.Replace(text, "[redacted]"); }
            catch { /* the match timed out; the built-in scrubber still runs */ }
        }
        return AuditExport.RedactText(text);
    }

    /// <summary>Observation is read-only, but a policy that forbids the host must forbid its output too.</summary>
    private static string? ReadRefusal(TerminalPane pane)
    {
        try
        {
            var store = App.Instance?.Store;
            if (store is null) return "the session store is not loaded";
            AgentPolicy.Require(pane.Session, store.Settings);
            AgentPolicyFile.Require(pane.Session, "exec", null);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static IEnumerable<TerminalPane> Panes()
        => App.Instance?.AllPanes() ?? Enumerable.Empty<TerminalPane>();

    private static TerminalPane? FindPane(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var panes = Panes().ToList();
        bool Matches(TerminalPane pane) =>
            string.Equals(pane.Session.Id, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pane.Session.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pane.Session.DisplayTarget, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pane.Session.Host, key, StringComparison.OrdinalIgnoreCase);
        return panes.FirstOrDefault(pane => Matches(pane) && pane.IsVisible)
               ?? panes.FirstOrDefault(Matches);
    }

    // ---------------------------------------------------------------- client (MCP process)

    /// <summary>
    /// One request/response over the pipe. Null is never returned: a caller always gets a frame it can
    /// print, so an MCP client sees a sentence instead of an empty result when the GUI is not running.
    /// </summary>
    public static JsonObject Call(JsonObject request, int timeoutMs = 20000)
    {
        try
        {
            // Raw bytes, no StreamWriter/StreamReader: a writer disposed while a reader task is still
            // blocked (or vice versa) threw ObjectDisposedException from the dispose's flush even though
            // the request had run. One write, then read until the newline with a deadline.
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(800);
            var payload = Utf8.GetBytes(request.ToJsonString() + "\n");
            client.Write(payload, 0, payload.Length);
            client.Flush();

            var received = new List<byte>();
            var deadline = Environment.TickCount64 + timeoutMs;
            var buffer = new byte[8192];
            while (Environment.TickCount64 < deadline)
            {
                var read = client.ReadAsync(buffer, 0, buffer.Length);
                var left = (int)Math.Max(1, deadline - Environment.TickCount64);
                if (!read.Wait(left)) return Error("the GUI did not answer within " + timeoutMs + " ms");
                var count = read.Result;
                if (count <= 0) break;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        var line = Utf8.GetString(received.ToArray());
                        return JsonNode.Parse(line) as JsonObject ?? Error("the GUI sent a reply that is not JSON");
                    }
                    received.Add(buffer[i]);
                }
            }

            return received.Count > 0
                ? (JsonNode.Parse(Utf8.GetString(received.ToArray())) as JsonObject ?? Error("the GUI sent a reply that is not JSON"))
                : Error("the GUI closed the pipe without a reply");
        }
        catch (Exception ex)
        {
            return Error("SSH-it is not running, so there is no pane to observe (start the app first): "
                         + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static bool Available
    {
        get
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(200);
                return true;
            }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- json helpers

    private static string Text(JsonObject request, string name, string fallback = "")
        => request[name]?.GetValue<string>() ?? fallback;

    private static long Long(JsonObject request, string name, long fallback)
    {
        try { return request[name] is { } node ? node.GetValue<long>() : fallback; }
        catch { return fallback; }
    }

    private static bool Bool(JsonObject request, string name, bool fallback)
    {
        try { return request[name] is { } node ? node.GetValue<bool>() : fallback; }
        catch { return fallback; }
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject Blocked(string message)
        => new() { ["ok"] = false, ["blocked"] = true, ["error"] = message };
}
