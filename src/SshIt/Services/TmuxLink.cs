using System.Diagnostics;
using System.Windows.Threading;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// One tmux control-mode connection, shared by every pane of one tmux window. The protocol and the
/// transport are separate on purpose: <see cref="TmuxControlSession"/> is a raw pipe with no console,
/// and this type turns its events into something a WPF window can consume on the UI thread.
///
/// Commands are answered in the order they were issued, so a tracked send can be awaited with a FIFO
/// of pending replies. That is what makes `display-message -p …` a query rather than a subscription.
/// </summary>
public sealed class TmuxLink : IDisposable
{
    private readonly TmuxControlSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly List<PendingReply> _pending = new();
    private readonly List<TmuxEvent> _queue = new();
    private bool _flushScheduled;
    private bool _disposed;
    private bool _exitPublished;

    private TmuxLink(TmuxControlSession session, Dispatcher dispatcher, string description)
    {
        _session = session;
        _dispatcher = dispatcher;
        Description = description;
        _session.Received += Publish;
        _session.Closed += OnSessionClosed;
        _session.Begin();
    }

    /// <summary>The child's own last words when the connection ended without a reply (ssh's error).</summary>
    public string? LastError { get; private set; }

    /// <summary>Protocol events, already marshalled to the UI thread and in arrival order.</summary>
    public event Action<TmuxEvent>? Event;

    /// <summary>Raised once when the stream ends (`%exit` or the process closing), on the UI thread.</summary>
    public event Action<string>? Exited;

    /// <summary>What this link is attached to, for a window title and the status bar.</summary>
    public string Description { get; }

    /// <summary>The last layout tmux reported.</summary>
    public TmuxLayout? Layout { get; private set; }

    public bool Running => !_disposed && _session.Running;

    /// <summary>`wsl.exe -d Ubuntu -e tmux -CC attach` — the test target (WSL has no sshd).</summary>
    public static TmuxLink StartLocal(string? distro, string? sessionName, Dispatcher dispatcher)
    {
        var psi = new ProcessStartInfo("wsl.exe");
        if (!string.IsNullOrWhiteSpace(distro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro.Trim()); }
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("tmux");
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add("attach");
        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(sessionName.Trim());
        }

        var label = string.IsNullOrWhiteSpace(sessionName) ? "local tmux" : "local tmux · " + sessionName.Trim();
        return new TmuxLink(TmuxControlSession.Start(psi), dispatcher, label);
    }

    /// <summary>`ssh -tt host tmux -CC attach` — the production transport.</summary>
    public static TmuxLink StartSsh(Session session, string? secret, string? sessionName, Dispatcher dispatcher)
    {
        var askPass = string.IsNullOrEmpty(secret) ? null : CredentialVault.StoreAskPass(session.Id, secret!);
        var psi = new ProcessStartInfo(SshLauncher.SshExe)
        {
            Arguments = SshLauncher.BuildTmuxArguments(session, sessionName)
        };
        foreach (var pair in SshLauncher.BuildEnvironment(session, askPass)) psi.Environment[pair.Key] = pair.Value;

        var label = $"tmux · {session.DisplayTarget}"
                    + (string.IsNullOrWhiteSpace(sessionName) ? "" : " · " + sessionName.Trim());
        return new TmuxLink(TmuxControlSession.Start(psi), dispatcher, label);
    }

    /// <summary>One command, one line; the caller does not wait.</summary>
    public void Send(string command)
    {
        if (_disposed) return;
        try { _session.Send(command); }
        catch (Exception ex) { Log.Warn("tmux send: " + ex.Message); FailPending(ex.Message); }
    }

    /// <summary>
    /// Sends a command and completes with its `%end`/`%error` text. Responses are FIFO, but tmux can
    /// send an UNSOLICITED `%begin/%end` pair right after attach; a marker in the response is the only
    /// way to be sure the text belongs to this command rather than to the session handshake.
    /// </summary>
    public async Task<string> QueryAsync(string command, int timeoutMs = 10000, string? marker = null)
    {
        var reply = new PendingReply(marker);
        lock (_gate) _pending.Add(reply);
        Send(command);

        var finished = await Task.WhenAny(reply.Tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(true);
        if (finished != reply.Tcs.Task)
        {
            // Remove the reply the timeout orphaned. Left in the list it would swallow the next
            // command's End and every later query would wait for a response that already went by.
            lock (_gate) _pending.Remove(reply);
            throw new TimeoutException("tmux did not answer: " + command);
        }
        return await reply.Tcs.Task.ConfigureAwait(true);
    }

    private sealed class PendingReply
    {
        public PendingReply(string? marker) => Marker = marker;
        public string? Marker { get; }
        public TaskCompletionSource<string> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Publish(TmuxEvent produced)
    {
        if (produced.Kind == TmuxEventKind.Exit) _exitPublished = true;
        if (produced.Kind is TmuxEventKind.End or TmuxEventKind.Error) CompleteNext(produced.Text);
        if (produced.Kind == TmuxEventKind.LayoutChange)
        {
            try { Layout = TmuxLayout.Parse(produced.Text); }
            catch (FormatException ex) { Log.Warn("tmux layout: " + ex.Message); }
        }

        var schedule = false;
        lock (_gate)
        {
            _queue.Add(produced);
            if (!_flushScheduled) { _flushScheduled = true; schedule = true; }
        }
        if (schedule) _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Flush));
    }

    private void Flush()
    {
        List<TmuxEvent> batch;
        lock (_gate)
        {
            batch = new List<TmuxEvent>(_queue);
            _queue.Clear();
            _flushScheduled = false;
        }

        foreach (var produced in batch)
        {
            try { Event?.Invoke(produced); }
            catch (Exception ex) { Log.Warn("tmux event handler: " + ex.Message); }

            if (produced.Kind == TmuxEventKind.Exit)
            {
                var reason = string.IsNullOrWhiteSpace(produced.Text) ? "detached" : produced.Text.Trim();
                try { Exited?.Invoke(reason); }
                catch (Exception ex) { Log.Warn("tmux exit handler: " + ex.Message); }
            }
        }
    }

    private void CompleteNext(string text)
    {
        PendingReply? next = null;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            var head = _pending[0];
            // A marked query ignores every unprompted reply until its own text arrives.
            if (head.Marker is { } marker && !text.Contains(marker, StringComparison.Ordinal)) return;
            next = head;
            _pending.RemoveAt(0);
        }
        next.Tcs.TrySetResult(text);
    }

    /// <summary>
    /// The stream ended. A dead ssh leaves no `%exit` behind, so a waiting query must be failed here
    /// or the window sits in "Attaching…" until its timeout and reports the timeout instead of the
    /// reason ssh already printed.
    /// </summary>
    private void OnSessionClosed()
    {
        if (_disposed) return;
        var tail = _session.StderrTail;
        LastError = string.IsNullOrWhiteSpace(tail) ? "the tmux connection closed" : tail;
        FailPending(LastError);
        if (!_exitPublished)
        {
            _exitPublished = true;
            Publish(new TmuxEvent { Kind = TmuxEventKind.Exit, Text = LastError });
        }
    }

    private void FailPending(string reason)
    {
        List<PendingReply> pending;
        lock (_gate)
        {
            pending = _pending.ToList();
            _pending.Clear();
        }
        foreach (var reply in pending) reply.Tcs.TrySetException(new InvalidOperationException(reason));
    }

    /// <summary>The last resort for a detached control stream: the session's own events are gone.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FailPending("the tmux connection closed");
        try { _session.Dispose(); } catch (Exception ex) { Log.Warn("tmux link dispose: " + ex.Message); }
    }
}
