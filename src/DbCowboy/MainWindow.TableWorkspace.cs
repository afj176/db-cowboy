using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DbCowboy;

/// <summary>
/// Tables: a schema explorer beside one open table. The table has five views (data, columns, indexes,
/// foreign keys, relationships); row edits are staged locally and only run after Review and apply.
/// </summary>
public sealed partial class MainWindow
{
    Action? tableOpened;
    Action<int>? selectTableView;

    FrameworkElement BuildTableWorkspace()
    {
        bool browsing = true;
        int selected = 0;
        Action arrange = () => { };

        // ---- Explorer: search, schema tree, refresh ----
        AutomationProperties.SetName(filter, "Search tables by schema or name");
        AutomationProperties.SetName(tree, "Database tables by schema");
        BrandControl(filter); BrandControl(tree);
        var back = Button("Back to table", (_, _) => { browsing = false; arrange(); });
        var refreshSchema = IconButton(Symbol.Refresh, "Refresh schema", RefreshClick);
        ToolTipService.SetToolTip(refreshSchema, "Read tables, columns and relationships from the database again");
        var explorerTitle = new Grid();
        explorerTitle.Children.Add(Eyebrow("Tables"));
        refreshSchema.HorizontalAlignment = HorizontalAlignment.Right;
        explorerTitle.Children.Add(refreshSchema);
        ((FrameworkElement)explorerTitle.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        var explorerLayout = new Grid { RowSpacing = 8 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) explorerLayout.RowDefinitions.Add(new() { Height = height });
        explorerLayout.Children.Add(explorerTitle);
        Grid.SetRow(back, 1); explorerLayout.Children.Add(back);
        Grid.SetRow(filter, 2); explorerLayout.Children.Add(filter);
        Grid.SetRow(tree, 3); explorerLayout.Children.Add(tree);
        var explorer = Surface(explorerLayout, 12);

        // ---- Page header ----
        var title = Display("Tables", 30);
        var description = Muted("", 14);
        var browse = ToolButton(Symbol.List, "Hide table list", (_, _) => { browsing = !browsing; arrange(); }, "Show or hide the table list");
        var header = PageHeader(title, description, browse);

        // ---- View switch and the tools that act on the loaded view ----
        var modes = new (string Label, string Name, string Description)[]
        {
            ("Data", "Browse data", "View and edit loaded table rows"),
            ("Columns", "Inspect columns", "Names, types, defaults and nullability"),
            ("Indexes", "Inspect indexes", "Index definitions for this table"),
            ("Foreign keys", "Inspect foreign keys", "Declared links to other tables"),
            ("Relationships", "Explore relationships", "Follow linked tables in the diagram")
        };
        var views = new ViewSwitcher { UseSegments = true };
        foreach (var mode in modes) views.Items.Add(new ViewOption { Header = mode.Label });
        AutomationProperties.SetName(views, "Table views");
        ToolTipService.SetToolTip(views, "Alt+1 to Alt+5 switch views");
        var grids = new ResultGrid?[] { tableGrid, columnsGrid, indexesGrid, foreignKeysGrid, null };
        foreach (var grid in grids.OfType<ResultGrid>()) grid.NoRowsMessage = "No rows are available in this view.";

        var filterRows = ToolButton(Symbol.Filter, "Filter", (_, _) => grids[selected]?.FocusSearch(), "Find text in the loaded rows. Right-click a column header for a column filter.");
        AutomationProperties.SetName(filterRows, "Filter loaded rows");
        var sort = ToolButton(Symbol.Sort, "Sort", (sender, _) => grids[selected]?.ShowSortMenu((FrameworkElement)sender), "Sort the loaded rows. Database paging order is unchanged.");
        AutomationProperties.SetName(sort, "Sort loaded rows");
        var refresh = ToolButton(Symbol.Refresh, "Refresh", async (sender, e) =>
        {
            if (current == null || operation != null) return;
            if (selected == 0) ReloadClick(sender, e);
            else await Work(async ct => { await Structure(ct); tableOpened?.Invoke(); status.Text = "Table structure refreshed"; });
        }, "Read this view from the database again");
        AutomationProperties.SetName(refresh, "Refresh table view");

        var more = new DropDownButton { Content = "More", Padding = new Thickness(12, 7, 10, 7), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(more, "More table commands");
        var moreMenu = Fluent.Acrylic(new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight });
        more.Flyout = moreMenu;
        var export = new MenuFlyoutItem { Text = "Export this page…", Icon = new SymbolIcon(Symbol.Download) };
        export.Click += (_, _) => Export(tableGrid.VisibleTable);
        moreMenu.Items.Add(export);
        var openInSql = new MenuFlyoutItem { Text = "Query this table in SQL", Icon = new SymbolIcon(Symbol.Edit) };
        openInSql.Click += (_, _) => { if (current != null) OpenSqlDraft(current.Name, $"SELECT *\rFROM {current.Qualified}\rLIMIT 100;"); };
        moreMenu.Items.Add(openInSql);

        var viewBar = CommandRow([views], [filterRows, sort, refresh, more]);
        var viewNote = Muted("", 13);

        // ---- Data view: paging and staging over the grid, inside one card ----
        var previous = IconButton(Symbol.Back, "Previous page", PreviousClick);
        var next = IconButton(Symbol.Forward, "Next page", NextClick);
        pageSummary.VerticalAlignment = VerticalAlignment.Center; pageSummary.TextWrapping = TextWrapping.NoWrap;
        var insert = ToolButton(Symbol.Add, "Insert row", InsertRowClick, "Stage a new row. Nothing is written until you review and apply.");
        var edit = ToolButton(Symbol.Edit, "Edit row", EditRowClick, "Stage changes to the selected row");
        AutomationProperties.SetName(edit, "Stage edit");
        var delete = ToolButton(Symbol.Delete, "Delete rows", DeleteRowsClick, "Stage deletion of the selected rows");
        AutomationProperties.SetName(delete, "Stage delete");
        ToolTipService.SetToolTip(tableReadOnly, "On: this workspace cannot write. Turn it off to stage edits; they are still reviewed before they run.");
        var editBar = CommandRow([previous, next, pageSummary], [insert, edit, delete, ToolSeparator(), tableReadOnly]);
        editBar.Padding = new Thickness(10, 8, 12, 8);

        // One strip under the bar: why editing is unavailable, or what is staged and the two ways out of it.
        var editNote = Muted("", 13);
        editNote.VerticalAlignment = VerticalAlignment.Center;
        changeSummary.VerticalAlignment = VerticalAlignment.Center; changeSummary.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var pendingActions = Row(applyChangesButton!, discardChangesButton!);
        var stagingStrip = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 8, 12, 8) };
        stagingStrip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        stagingStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        stagingStrip.Children.Add(editNote); stagingStrip.Children.Add(changeSummary);
        Grid.SetColumn(pendingActions, 1); stagingStrip.Children.Add(pendingActions);
        var stagingBand = Themed(new Border { Child = stagingStrip, BorderThickness = new Thickness(0, 1, 0, 1) }, (band, dark) =>
        {
            if (HighContrast) { band.ClearValue(Border.BackgroundProperty); return; }
            band.BorderBrush = BrandUi.LineBrush(dark);
            band.Background = new SolidColorBrush(Brand.Fade(ToneColor(pendingChanges.Count > 0 ? Tone.Accent : Tone.Neutral, dark), 22));
        });

        var dataLayout = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) dataLayout.RowDefinitions.Add(new() { Height = height });
        dataLayout.Children.Add(editBar);
        Grid.SetRow(stagingBand, 1); dataLayout.Children.Add(stagingBand);
        Grid.SetRow(tableGrid, 2); dataLayout.Children.Add(tableGrid);

        var surfaces = new FrameworkElement[] { Surface(dataLayout, 0), Surface(columnsGrid, 0), Surface(indexesGrid, 0), Surface(foreignKeysGrid, 0), Surface(relationships, 0) };
        var host = new Grid();
        foreach (var surface in surfaces) host.Children.Add(surface);

        // ---- Nothing open: one message and the next step, no toolbars ----
        var emptyHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var emptyCard = Surface(emptyHost, 0);

        var workLayout = new Grid { RowSpacing = 10 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) workLayout.RowDefinitions.Add(new() { Height = height });
        workLayout.Children.Add(viewBar);
        Grid.SetRow(viewNote, 1); workLayout.Children.Add(viewNote);
        Grid.SetRow(host, 2); workLayout.Children.Add(host);
        var work = new Grid();
        work.Children.Add(workLayout); work.Children.Add(emptyCard);

        // Enables the staging buttons only when staging can succeed, and says why when it cannot.
        void RenderStaging()
        {
            bool isView = current?.Kind is "v" or "m";
            bool locked = readOnly.IsChecked == true;
            bool canInsert = current != null && !isView && !locked;
            insert.IsEnabled = canInsert;
            edit.IsEnabled = delete.IsEnabled = canInsert && keys.Count > 0;
            previous.IsEnabled = current != null && page > 0;
            next.IsEnabled = current != null && tableGrid.Table?.Rows.Count >= 250;
            editNote.Text = current == null ? ""
                : isView ? "Views are browsed here. Write to them with an explicit statement in SQL."
                : locked ? "Read-only is on. Turn it off to stage inserts, edits and deletes."
                : keys.Count == 0 ? "No primary key: rows can be inserted here, but not edited or deleted."
                : "";
            bool pending = pendingChanges.Count > 0;
            // Staged changes take the strip over; the reason line returns when they are gone.
            changeSummary.Visibility = pendingActions.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            editNote.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
            stagingBand.Visibility = pending || editNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!HighContrast) stagingBand.Background = new SolidColorBrush(Brand.Fade(ToneColor(pending ? Tone.Accent : Tone.Neutral, stagingBand.ActualTheme == ElementTheme.Dark), 22));
        }

        void RenderView()
        {
            for (int i = 0; i < surfaces.Length; i++) surfaces[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
            filterRows.IsEnabled = sort.IsEnabled = current != null && grids[selected] != null;
            // Filter and sort act on a grid; the relationship diagram has none, so they leave the bar.
            filterRows.Visibility = sort.Visibility = grids[selected] != null ? Visibility.Visible : Visibility.Collapsed;
            export.IsEnabled = openInSql.IsEnabled = current != null;
            viewNote.Text = selected switch
            {
                1 => $"{currentColumns.Count} columns · read-only catalog details",
                2 => $"{indexesGrid.Table?.Rows.Count ?? 0} indexes · scans and entries read are counted since statistics were last reset",
                3 => $"{foreignKeysGrid.Table?.Rows.Count ?? 0} foreign keys · incoming and outgoing links",
                4 => "Pan or zoom the diagram. Open a linked table to browse its rows.",
                _ => ""
            };
            viewNote.Visibility = viewNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderStaging();
        }

        void Select(int index)
        {
            selected = Math.Clamp(index, 0, modes.Length - 1);
            if (views.SelectedIndex != selected) views.SelectedIndex = selected;
            RenderView();
        }
        views.SelectionChanged += (_, _) => { if (views.SelectedIndex >= 0 && views.SelectedIndex != selected) Select(views.SelectedIndex); };

        void RenderHeader()
        {
            bool open = current != null;
            workLayout.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            emptyCard.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            if (current is { } relation)
            {
                title.Text = relation.Name;
                string kind = relation.Kind switch { "v" => "view", "m" => "materialized view", "p" => "partitioned table", "f" => "foreign table", _ => "table" };
                string size = relation.Estimate >= 0 ? $" · about {relation.Estimate:N0} rows" : "";
                string key = keys.Count > 0 ? " · primary key " + string.Join(", ", keys) : relation.Kind is "v" or "m" ? "" : " · no primary key";
                description.Text = $"{relation.Schema} · {kind}{size}{key}";
                return;
            }
            title.Text = "Tables";
            if (db == null)
            {
                description.Text = "Browse rows, structure and relationships. Stage edits, then review and apply.";
                emptyHost.Content = EmptyState("02-wait-what", "No database connected", "Connect to Postgres and its tables will be listed here.", Button("Connections", ConnectionsClick, true));
            }
            else if (relations.Count == 0)
            {
                description.Text = db.Profile.Database + " · no tables or views visible to this role";
                emptyHost.Content = EmptyState("02-wait-what", "Nothing to browse yet", "This role cannot see any tables or views in this database. Create one in SQL, then refresh the schema.", Button("Open SQL", (_, _) => Navigate("Query"), true), Button("Refresh schema", RefreshClick));
            }
            else
            {
                description.Text = $"{db.Profile.Database} · {relations.Count:N0} tables and views";
                emptyHost.Content = EmptyState("01-got-you", "Pick a table", "Choose one from the list to browse its rows, columns, indexes and relationships.");
            }
        }

        // ---- Page layout: explorer | open table ----
        // ---- Size and maintenance facts for the open table, straight from the catalog ----
        var factsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        CancellationTokenSource? factsLoad = null;
        // The strip gives way in a short window so the rows keep usable room; arrange() decides whether it fits.
        bool factsReady = false, roomForFacts = true;
        void ShowFacts() => factsHost.Visibility = factsReady && roomForFacts ? Visibility.Visible : Visibility.Collapsed;
        async Task LoadFacts()
        {
            factsLoad?.Cancel(); factsLoad?.Dispose(); factsLoad = new();
            var ct = factsLoad.Token;
            var relation = current; var active = db;
            // Views have no storage of their own, so there is nothing to report for them.
            if (relation == null || active == null || relation.Kind == "v") { factsReady = false; ShowFacts(); return; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var facts = await active.TableFacts(relation, timeout.Token);
                if (ct.IsCancellationRequested || facts == null || current?.Qualified != relation.Qualified) return;
                factsHost.Content = StatStrip(
                    ("Rows", facts.LiveRows.ToString("N0"), $"Live rows counted by the statistics collector. Dead rows waiting for vacuum: {facts.DeadRows:N0}." + (relation.Kind == "p" ? " Rows and sizes are summed over all partitions." : "")),
                    ("Total size", facts.TotalSize, "Table data, TOAST and every index."),
                    ("Table data", facts.TableSize, "The table itself with its TOAST data, without indexes."),
                    ("Indexes", $"{facts.Indexes} · {facts.IndexSize}", "How many indexes the table has and their combined size. The Indexes view shows how often each is used."),
                    ("Last analyzed", facts.LastAnalyzed ?? "never", "When planner statistics were last gathered, by ANALYZE or autovacuum."),
                    ("Last vacuumed", facts.LastVacuumed ?? "never", "When dead rows were last cleaned up, by VACUUM or autovacuum."));
                factsReady = true; ShowFacts();
            }
            // The strip is supplementary: a role that may not read the statistics still browses the table.
            catch { if (!ct.IsCancellationRequested) { factsReady = false; ShowFacts(); } }
        }

        var body = new Grid { RowSpacing = 10 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(header);
        Grid.SetRow(factsHost, 1); body.Children.Add(factsHost);
        Grid.SetRow(work, 2); body.Children.Add(work);
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(240) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(explorer);
        Grid.SetColumn(body, 1); layout.Children.Add(body);

        arrange = () =>
        {
            // Under 760 px the explorer and the table take turns instead of sharing the width.
            bool compact = layout.ActualWidth < 760;
            roomForFacts = layout.ActualHeight <= 0 || layout.ActualHeight >= 620; ShowFacts();
            layout.ColumnDefinitions[0].Width = browsing && !compact ? new GridLength(240) : new GridLength(0);
            layout.ColumnSpacing = browsing && !compact ? 16 : 0;
            Grid.SetColumn(explorer, compact ? 1 : 0);
            explorer.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
            body.Visibility = compact && browsing ? Visibility.Collapsed : Visibility.Visible;
            back.Visibility = compact && current != null ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)((StackPanel)browse.Content).Children[1]).Text = browsing ? "Hide table list" : "Show table list";
            // The accessible name matches the visible label, so speech users can say what they see.
            AutomationProperties.SetName(browse, browsing ? "Hide table list" : "Show table list");
        };
        layout.SizeChanged += (_, _) => arrange();

        for (int i = 0; i < modes.Length; i++)
        {
            int index = i;
            var shortcut = new KeyboardAccelerator { Key = (VirtualKey)((int)VirtualKey.Number1 + i), Modifiers = VirtualKeyModifiers.Menu, ScopeOwner = layout };
            shortcut.Invoked += (_, e) => { if (current == null) return; browsing = false; Select(index); arrange(); views.FocusSelector(); e.Handled = true; };
            layout.KeyboardAccelerators.Add(shortcut);
        }

        string? opened = null;
        tableOpened = () =>
        {
            string? key = current == null ? null : current.Schema + "." + current.Name;
            if (key != opened) selected = 0;
            opened = key;
            // With nothing open the list is the only way forward, so it comes back.
            if (current == null) browsing = true;
            // A narrow window shows either the list or the table, so opening a table switches to it.
            else if (layout.ActualWidth < 760) browsing = false;
            RenderHeader(); Select(selected); arrange(); _ = LoadFacts();
        };
        selectTableView = index => { browsing = false; Select(index); arrange(); };
        refreshTableChrome = () => { RenderHeader(); RenderStaging(); };
        readOnly.Checked += (_, _) => RenderStaging();
        readOnly.Unchecked += (_, _) => RenderStaging();
        tableGrid.ViewChanged += RenderStaging;
        RenderHeader(); Select(0);
        return layout;
    }
}
