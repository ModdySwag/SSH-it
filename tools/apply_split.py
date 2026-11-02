import pathlib

root = pathlib.Path(r"C:\Users\Moddy\ssh-client\src\SshIt")

def patch(rel, pairs):
    p = root / rel
    s = p.read_text(encoding="utf-8")
    for old, new, tag in pairs:
        if old not in s:
            print("MISS", rel, tag)
            continue
        s = s.replace(old, new, 1)
        print("ok", tag)
    p.write_text(s, encoding="utf-8")

patch("MainWindow.xaml.cs", [
(
'''    private sealed class TabEntry
    {
        public required Session Session { get; init; }
        public required TerminalPane Pane { get; init; }
        public required Border Header { get; init; }
        public required TextBlock TitleText { get; init; }
        public required Ellipse Dot { get; init; }
    }''',
'''    private sealed class TabEntry
    {
        public required Session Session { get; init; }
        public required TerminalPane Pane { get; set; }
        public required Border Header { get; init; }
        public required TextBlock TitleText { get; init; }
        public required Ellipse Dot { get; init; }

        /// <summary>Every pane in this tab: one normally, two when split.</summary>
        public List<TerminalPane> Panes { get; } = new();

        /// <summary>What the tab host shows: a single pane, or a split grid.</summary>
        public Grid Host { get; set; } = new();

        public bool SplitVertical { get; set; } = true;
    }''',
"tab entry"),

(
'''        var header = BuildTabHeader(session, pane, out var titleText, out var dot);
        var tab = new TabEntry { Session = session, Pane = pane, Header = header, TitleText = titleText, Dot = dot };
        _tabs.Add(tab);''',
'''        var header = BuildTabHeader(session, pane, out var titleText, out var dot);
        var host = new Grid();
        host.Children.Add(pane);
        var tab = new TabEntry
        {
            Session = session, Pane = pane, Header = header, TitleText = titleText, Dot = dot, Host = host
        };
        tab.Panes.Add(pane);
        _tabs.Add(tab);''',
"open session host"),

('        TerminalHost.Content = entry.Pane;', '        TerminalHost.Content = entry.Host;', "select tab host"),

('        entry.Pane.Dispose();\n        _tabs.Remove(entry);',
 '        foreach (var p in entry.Panes) p.Dispose();\n        _tabs.Remove(entry);',
 "close all panes"),

('        var entry = _tabs.FirstOrDefault(t => t.Pane == pane);',
 '        var entry = _tabs.FirstOrDefault(t => t.Panes.Contains(pane));',
 "header lookup"),
])

patch("MainWindow.xaml", [
('''                        <MenuItem Header="_Full screen" InputGestureText="F11" Click="FullScreen_Click" />''',
'''                        <MenuItem Header="_Full screen" InputGestureText="F11" Click="FullScreen_Click" />
                        <Separator />
                        <MenuItem Header="Split _right" InputGestureText="Ctrl+Shift+D" Click="SplitRight_Click" />
                        <MenuItem Header="Split _down" InputGestureText="Ctrl+Shift+E" Click="SplitDown_Click" />
                        <MenuItem Header="Close _pane" InputGestureText="Ctrl+Shift+Q" Click="ClosePane_Click" />''',
 "view menu"),
])

patch("MainWindow.Commands.cs", [
('''        if (ctrl && shift && e.Key == Key.W) { CloseTab_Click(this, new RoutedEventArgs()); e.Handled = true; return; }''',
'''        if (ctrl && shift && e.Key == Key.W) { CloseTab_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.D) { SplitPane(vertical: true); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.E) { SplitPane(vertical: false); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Q) { ClosePane_Click(this, new RoutedEventArgs()); e.Handled = true; return; }''',
 "split shortcuts"),
])
