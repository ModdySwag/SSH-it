namespace SshIt.Services;

/// <summary>
/// The in-app changelog behind Help > What's new. It ships with the build and makes no network call —
/// a "check the web site" link would be a lie on a machine that has never been online, and a silent
/// updater is a bigger promise than this app wants to make.
/// </summary>
public static class Changelog
{
    public const string Text =
        "1.0.29 — the current build\n" +
        "\n" +
        "Agent boundary\n" +
        "  · Signed, expiring leases and one-command approvals bound to a pane's pty.\n" +
        "  · A signed policy with two-person tokens for destructive commands.\n" +
        "  · A 19-tool MCP server (2025-06-18) with read-only/destructive annotations, structured\n" +
        "    results and refusals returned as tool errors.\n" +
        "\n" +
        "Daily work\n" +
        "  · Command palette on Ctrl+Shift+P: sessions, the ledger, snippets, services, tunnels, undo\n" +
        "    points, every menu action and every setting.\n" +
        "  · Command ledger: every finished block is searchable by its output, not just its command.\n" +
        "  · Workspaces: save the tabs you have open and get them back later.\n" +
        "  · Transfer queue, named service tunnels, GitOps inventory, block diff and watch mode.\n" +
        "\n" +
        "Proof and accessibility\n" +
        "  · A signed evidence chain over finished blocks, transfers and tunnels.\n" +
        "  · Screen-reader announcements for command results and connection state.\n" +
        "  · High contrast, full keyboard access, and a terminal that publishes itself through UIA.\n" +
        "\n" +
        "This build is free and open source. There is no telemetry, no account and no cloud.";
}
