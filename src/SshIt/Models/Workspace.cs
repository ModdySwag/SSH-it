namespace SshIt.Models;

/// <summary>One tab in a saved workspace: which session, and how its panes were arranged.</summary>
public sealed class WorkspaceTab
{
    public string SessionId { get; set; } = "";
    public bool Split { get; set; }
    public bool Vertical { get; set; } = true;
    public bool Broadcast { get; set; }
}

/// <summary>
/// A named layout: the tabs a window had, which one was in front, whether a tab was split, and which
/// tool panel was open. It stores session ids, not connection details — opening a workspace connects
/// with whatever the session says today, so an edited host or key is picked up rather than replayed.
/// </summary>
public sealed class Workspace
{
    public string Name { get; set; } = "";
    public List<WorkspaceTab> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public string Tool { get; set; } = "Files";
    public string Created { get; set; } = "";
    public string Updated { get; set; } = "";

    /// <summary>
    /// Builds a workspace from the live tabs, clamping the active index and dropping tabs with no
    /// session. Pure on purpose: the window passes plain tuples and the result is testable.
    /// </summary>
    public static Workspace Capture(string name, IEnumerable<WorkspaceTab> tabs, int activeIndex, string tool)
    {
        var workspace = new Workspace
        {
            Name = (name ?? "").Trim(),
            Tool = string.IsNullOrWhiteSpace(tool) ? "Files" : tool,
            Created = DateTime.UtcNow.ToString("O"),
            Updated = DateTime.UtcNow.ToString("O")
        };

        foreach (var tab in tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.SessionId)) continue;
            workspace.Tabs.Add(new WorkspaceTab
            {
                SessionId = tab.SessionId,
                Split = tab.Split,
                Vertical = tab.Vertical,
                Broadcast = tab.Broadcast
            });
        }

        workspace.ActiveIndex = workspace.Tabs.Count == 0
            ? 0
            : Math.Clamp(activeIndex, 0, workspace.Tabs.Count - 1);
        return workspace;
    }

    public string Summary()
    {
        var splits = Tabs.Count(tab => tab.Split);
        return Name + " — " + Tabs.Count + " tab(s)" + (splits > 0 ? ", " + splits + " split" : "")
               + ", " + Tool + " panel";
    }

    /// <summary>
    /// What opening a workspace should say. Pure so the wording is testable: a layout whose sessions
    /// have been deleted must say so — silently opening fewer tabs than were saved is the one outcome
    /// a user cannot see coming.
    /// </summary>
    public static string OpenSummary(string name, int opened, int missing)
    {
        var label = "\"" + (name ?? "").Trim() + "\": ";
        if (opened == 0)
            return "Workspace " + label + (missing > 0
                ? "none of its " + missing + " session(s) exist any more."
                : "no tabs to open.");

        return "Workspace " + label + opened + " tab(s) opened"
               + (missing > 0 ? ", " + missing + " session(s) no longer exist" : "") + ".";
    }
}
