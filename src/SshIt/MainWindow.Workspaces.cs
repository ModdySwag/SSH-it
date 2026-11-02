using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;
using SshIt.Views;

namespace SshIt;

/// <summary>
/// Workspaces: name the tabs a window has, and get them back later. A workspace stores session ids,
/// not credentials or host strings, so opening one reconnects with today's settings — a fix to a host
/// or a rotated key is picked up instead of being replayed from an old snapshot.
///
/// Save and open are GUI-only on purpose: a headless process has no tabs to capture and no window to
/// fill, so `--workspace-save` would always record nothing. The CLI can list and delete them.
/// </summary>
public partial class MainWindow
{
    private void SaveWorkspaceInteractive()
    {
        if (_tabs.Count == 0)
        {
            StatusMessage.Text = "Nothing to save — open a tab first.";
            return;
        }

        var suggested = _active?.Session.Name ?? "Workspace";
        var name = NamePromptWindow.Ask(this, "Save workspace", "Name this layout:", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;

        var tabs = _tabs.Select(tab => new WorkspaceTab
        {
            SessionId = tab.Session.Id,
            Split = tab.Panes.Count > 1,
            Vertical = tab.SplitVertical,
            Broadcast = tab.Broadcast
        });

        var active = _active is null ? 0 : Math.Max(0, _tabs.IndexOf(_active));
        var workspace = Workspace.Capture(name, tabs, active, _tool.ToString());
        WorkspaceStore.Store(workspace);
        BuildWorkspaceMenu();
        StatusMessage.Text = "Workspace \"" + workspace.Name + "\" saved — " + workspace.Tabs.Count + " tab(s), "
                             + workspace.Tool + " panel.";
    }

    /// <summary>Opens every tab the workspace names, re-splits the ones that were split, then focuses
    /// the tab that was in front. A session that no longer exists is counted, never invented.</summary>
    private void ApplyWorkspace(Workspace workspace)
    {
        if (workspace.Tabs.Count == 0)
        {
            StatusMessage.Text = "Workspace \"" + workspace.Name + "\" has no tabs.";
            return;
        }

        var opened = new List<TabEntry>();
        // ActiveIndex addresses the SAVED list; `opened` omits tabs whose session is gone. Keeping the
        // source index with each entry is what makes "focus the tab that was in front" true.
        var openedAt = new List<int>();
        var missing = 0;

        for (var tabIndex = 0; tabIndex < workspace.Tabs.Count; tabIndex++)
        {
            var tab = workspace.Tabs[tabIndex];
            var session = _store.Sessions.FirstOrDefault(s => s.Id == tab.SessionId);
            if (session is null)
            {
                missing++;
                continue;
            }

            var before = _tabs.Count;
            OpenSession(session, forceNewTab: true);
            // If OpenSession refuses (locked, duplicate suppressed), LastOrDefault is a PREVIOUS tab:
            // it would be added to the workspace again and possibly re-split.
            var entry = _tabs.Count > before ? _tabs[before] : null;
            if (entry is not null)
            {
                if (tab.Split && entry.Panes.Count == 1) SplitPane(tab.Vertical);
                if (tab.Broadcast) SetBroadcast(entry, true);
                opened.Add(entry);
                openedAt.Add(tabIndex);
            }
        }

        if (opened.Count > 0)
        {
            var position = openedAt.IndexOf(workspace.ActiveIndex);
            SelectTab(opened[position >= 0 ? position : 0]);
        }

        if (Enum.TryParse<ToolKind>(workspace.Tool, ignoreCase: true, out var tool)) ShowTool(tool);

        var summary = Workspace.OpenSummary(workspace.Name, opened.Count, missing);
        StatusMessage.Text = summary;

        // The summary is the only record that a tab in the layout no longer exists. Every pane the
        // workspace just connected reports its state a few hundred milliseconds later and UpdateStatus
        // writes that activity over StatusMessage, so the sentence has to be said somewhere durable:
        // the live region, which is both read aloud and visible in the status bar. Posted late so the
        // connect notices land first and this is what stays.
        var announce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        announce.Tick += (_, _) =>
        {
            announce.Stop();
            // The window can be closed inside the 1200 ms; announcing into a dead live region is a
            // dispatcher callback on a torn-down tree.
            if (IsLoaded) Announcer.Announce(StatusAnnounce, summary);
        };
        announce.Start();
    }

    /// <summary>Rebuilt whenever the store changes, so the menu never shows a deleted workspace.</summary>
    private void BuildWorkspaceMenu()
    {
        MenuWorkspaces.Items.Clear();

        var save = new MenuItem { Header = "Save current tabs as…" };
        save.Click += (_, _) => SaveWorkspaceInteractive();
        MenuWorkspaces.Items.Add(save);

        var all = WorkspaceStore.Load();
        if (all.Count == 0) return;

        MenuWorkspaces.Items.Add(new Separator());
        foreach (var workspace in all)
        {
            var item = new MenuItem
            {
                Header = workspace.Name + "   (" + workspace.Tabs.Count + " tab"
                         + (workspace.Tabs.Count == 1 ? "" : "s") + ")",
                ToolTip = workspace.Summary()
            };
            var captured = workspace;
            item.Click += (_, _) => ApplyWorkspace(captured);
            MenuWorkspaces.Items.Add(item);
        }
    }
}
