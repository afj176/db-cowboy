using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DbCowboy;

/// <summary>
/// Activity: a snapshot of the sessions in the connected database (pg_stat_activity, as far as the role may see),
/// and the queries run from this computer.
/// </summary>
public sealed partial class MainWindow
{
    Guid? activitySession;   // session the snapshot was read from

    FrameworkElement BuildActivityPage()
    {
        var refresh = ToolButton(Symbol.Refresh, "Refresh sessions", ActivityClick, "Read pg_stat_activity for this database again", accent: true);
        var header = PageHeader("Activity", "Who is connected to this database right now, and the queries you have run from this computer.", refresh);

        // ---- Server sessions ----
        var sessionsTile = Tile("Sessions", "–", out var setSessions);
        var activeTile = Tile("Active", "–", out var setActive);
        var idleTile = Tile("Idle", "–", out var setIdle);
        var waitingTile = Tile("Waiting", "–", out var setWaiting);
        var tiles = Flow(150, sessionsTile, activeTile, idleTile, waitingTile);
        tiles.Visibility = Visibility.Collapsed;

        activityGrid.InitialArt = "01-got-you";
        activityGrid.NoRowsMessage = "No sessions are visible to this role.";
        var sessionsNote = Muted("", 13);
        var sessionsTitle = new StackPanel { Spacing = 2, Margin = new Thickness(16, 14, 16, 8) };
        sessionsTitle.Children.Add(Display("Server sessions", 17)); sessionsTitle.Children.Add(sessionsNote);
        var sessionsLayout = new Grid();
        sessionsLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sessionsLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        sessionsLayout.Children.Add(sessionsTitle);
        Grid.SetRow(activityGrid, 1); sessionsLayout.Children.Add(activityGrid);
        var sessionsCard = Surface(sessionsLayout, 0);

        // The tiles count the snapshot that is on screen; nothing is polled in the background.
        void RenderSnapshot()
        {
            var table = activityGrid.Table;
            tiles.Visibility = table == null ? Visibility.Collapsed : Visibility.Visible;
            if (table == null) { sessionsNote.Text = db == null ? "Connect to a database to see its sessions." : "A snapshot, taken when you ask for it. Nothing is polled in the background."; return; }
            activitySession = db?.SessionIdentity;
            var rows = table.Rows.Cast<DataRow>().ToList();
            string State(DataRow row) => table.Columns.Contains("state") && row["state"] != DBNull.Value ? Convert.ToString(row["state"]) ?? "" : "";
            int waiting = table.Columns.Contains("wait_event_type") ? rows.Count(r => Convert.ToString(r["wait_event_type"]) == "Lock") : 0;
            setSessions(rows.Count.ToString("N0"), "in this database");
            setActive(rows.Count(r => State(r) == "active").ToString("N0"), "running a statement");
            setIdle(rows.Count(r => State(r).StartsWith("idle")).ToString("N0"), "including idle in transaction");
            setWaiting(waiting.ToString("N0"), "blocked on a lock");
            sessionsNote.Text = $"Snapshot taken {DateTime.Now:T}. Visibility follows your database role.";
        }
        activityGrid.ViewChanged += RenderSnapshot;

        // ---- Query history ----
        AutomationProperties.SetName(historySearch, "Search query history");
        AutomationProperties.SetName(historyList, "Query history");
        BrandControl(historySearch); BrandControl(historyList);
        historySearch.Width = double.NaN; historySearch.MinWidth = 220; historySearch.MaxWidth = 320;
        historySearch.TextChanged += (_, _) => FilterHistory();
        var open = ToolButton(Symbol.OpenFile, "Open in new tab", HistoryLoadClick, "Open the selected query in a new SQL tab. It is not run.");
        AutomationProperties.SetName(open, "Open history query");
        var clear = ToolButton(Symbol.Delete, "Clear history", async (sender, e) =>
        {
            if (history.Count == 0 || !await Confirm("Clear query history?", $"Removes all {history.Count:N0} entries kept on this computer. Saved snippets and query tabs are not affected.", "Clear history")) return;
            ClearHistoryClick(sender, e);
        }, "Remove every entry from the history kept on this computer");

        // Each row is built in code: the statement on one line, then where and when it ran.
        historyList.DisplayMemberPath = "";
        historyList.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid/></DataTemplate>");
        historyList.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer.ContentTemplateRoot is not Grid cell || e.Item is not QueryLog entry) return;
            cell.Children.Clear();
            if (e.InRecycleQueue) return;
            string sql = string.Join(" ", entry.Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var statement = Text(sql, 13);
            statement.FontSize = 13; statement.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"); statement.TextWrapping = TextWrapping.NoWrap; statement.TextTrimming = TextTrimming.CharacterEllipsis;
            var detail = Muted($"{entry.Connection} · {entry.At:g} · {entry.Rows:N0} {(entry.Rows == 1 ? "row" : "rows")} · {entry.Milliseconds:N0} ms", 12);
            var row = new StackPanel { Spacing = 2, Margin = new Thickness(0, 7, 0, 8) };
            row.Children.Add(statement); row.Children.Add(detail);
            cell.Children.Add(row);
            ToolTipService.SetToolTip(cell, entry.Sql.Length > 1200 ? entry.Sql[..1200] + "…" : entry.Sql);
            AutomationProperties.SetName(e.ItemContainer, sql.Length > 160 ? sql[..160] : sql);
        };
        historyList.DoubleTapped += (sender, _) => HistoryLoadClick(sender, new());

        var historyCount = Muted("", 13);
        var historyEmpty = Muted("", 14);
        historyEmpty.HorizontalAlignment = HorizontalAlignment.Center; historyEmpty.VerticalAlignment = VerticalAlignment.Center; historyEmpty.Margin = new Thickness(24);
        void RenderHistory()
        {
            int shown = (historyList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
            historyCount.Text = history.Count == 0 ? "Kept on this computer. Avoid secrets in SQL text."
                : $"{shown:N0} of {history.Count:N0} kept on this computer. Avoid secrets in SQL text.";
            historyEmpty.Text = history.Count == 0 ? "Queries you run will be listed here." : "No query in the history matches this search.";
            historyEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.IsEnabled = history.Count > 0;
            open.IsEnabled = historyList.SelectedItem is QueryLog;
        }
        historyList.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, (_, _) => RenderHistory());
        historyList.SelectionChanged += (_, _) => RenderHistory();

        var historyHeading = new StackPanel { Spacing = 2 };
        historyHeading.Children.Add(Display("Query history", 17)); historyHeading.Children.Add(historyCount);
        var historyBar = CommandRow([historyHeading], [historySearch, open, clear]);
        historyBar.Padding = new Thickness(16, 14, 12, 8);
        var historyLayout = new Grid();
        historyLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        historyLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        historyLayout.Children.Add(historyBar);
        Grid.SetRow(historyList, 1); historyLayout.Children.Add(historyList);
        Grid.SetRow(historyEmpty, 1); historyLayout.Children.Add(historyEmpty);
        var historyCard = Surface(historyLayout, 0);

        // A snapshot belongs to the session it was read from; a new connection starts clean.
        refreshActivityChrome = () =>
        {
            if (activityGrid.Table != null && db?.SessionIdentity != activitySession) { activityGrid.Clear(); activitySession = null; }
            refresh.IsEnabled = db != null && operation == null;
            activityGrid.InitialTitle = db == null ? "No database connected" : "No snapshot yet";
            activityGrid.InitialMessage = db == null ? "Connect to Postgres, then refresh to see its sessions." : "Refresh sessions to see who is connected to " + db.Profile.Database + ".";
            RenderSnapshot();
        };

        var page = new Grid { RowSpacing = 14 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) }) page.RowDefinitions.Add(new() { Height = height });
        page.Children.Add(header);
        Grid.SetRow(tiles, 1); page.Children.Add(tiles);
        Grid.SetRow(sessionsCard, 2); page.Children.Add(sessionsCard);
        Grid.SetRow(historyCard, 3); page.Children.Add(historyCard);
        refreshActivityChrome(); RenderHistory();

        // On a short window the two cards keep a workable height and the page scrolls instead of squeezing them.
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) => page.Height = Math.Max(scroll.ActualHeight, 780);
        return scroll;
    }
}
