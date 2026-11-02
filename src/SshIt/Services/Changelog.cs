namespace SshIt.Services;

/// <summary>
/// The in-app changelog behind Help > What's new. It ships with the build and makes no network call —
/// a "check the web site" link would be a lie on a machine that has never been online, and a silent
/// updater is a bigger promise than this app wants to make.
/// </summary>
public static class Changelog
{
    public const string Text =
        "1.0.33 — the current build\n" +
        "\n" +
        "  · The port and timeout boxes fit five-digit values instead of clipping them.\n" +
        "\n" +
        "Network discovery\n" +
        "  · Session > Network scan finds SSH hosts on a network you name: a CIDR block, a\n" +
        "    range or one address; TCP connect on one port, up to 4,096 addresses, with the\n" +
        "    banner and host key of every host that answers.\n" +
        "  · Results become sessions in one click. A host and port already in the list is\n" +
        "    skipped, and nothing is saved until you add it.\n" +
        "\n" +
        "The terminal, on its own\n" +
        "  · Focus mode (Ctrl+Shift+Z) hides the sidebar, the tool panel and the status bar\n" +
        "    together, and puts them back exactly as they were.\n" +
        "\n" +
        "Release hygiene\n" +
        "  · The source now carries an MIT LICENSE file, and the winget manifest carries a\n" +
        "    real SHA-256 instead of a placeholder.\n" +
        "  · A diagnostic probe command containing spaces is quoted once, not twice.\n" +
        "\n" +
        "1.0.29 — fleet posture and the agent boundary\n" +
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
