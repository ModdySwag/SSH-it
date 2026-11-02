using System.Windows;
using SshIt.Models;
using SshIt.Views;

namespace SshIt;

/// <summary>
/// tmux control mode is a window, not a tab: the tmux server owns the panes and their layout, so
/// the tab machinery (one pane, one split tree, one ledger) does not describe it. A saved session
/// with Kind "Tmux" opens here from the tree, and Tools → tmux control mode… opens ad-hoc targets.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, TmuxWindow> _tmuxWindows = new();

    private void OpenTmuxWindow(Session session)
    {
        if (_tmuxWindows.TryGetValue(session.Id, out var open) && open.IsVisible)
        {
            open.Activate();
            return;
        }

        var window = new TmuxWindow(session, session.TmuxSession, _store.Settings);
        window.Closed += (_, _) => _tmuxWindows.Remove(session.Id);
        _tmuxWindows[session.Id] = window;
        _app.TrackSurface(window);
        window.Show();

        _store.Settings.AddRecent(session.Id);
        session.LastUsed = DateTime.Now;
        session.UseCount++;
        _store.SaveSessions();
        _app.UpdateJumpList();
    }

    private void Tmux_Click(object sender, RoutedEventArgs e)
    {
        var picker = new TmuxAttachWindow(_store) { Owner = this };
        if (picker.ShowDialog() != true || picker.Pick is not { } pick) return;

        var window = pick.SshSession is null
            ? new TmuxWindow(null, pick.TmuxSession, _store.Settings, pick.LocalDistro)
            : new TmuxWindow(pick.SshSession, pick.TmuxSession, _store.Settings);
        _app.TrackSurface(window);
        window.Show();
    }

    /// <summary>The tmux windows this main window opened, so a second open can bring one forward.</summary>
    internal IEnumerable<TmuxWindow> TmuxWindows => _tmuxWindows.Values;
}
