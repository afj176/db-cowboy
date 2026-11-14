using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DbCowboy;

/// <summary>
/// Extensions: what the connected server can install, what this database has installed, and what each
/// installed extension is doing here. Without a connection the page still works as a guide.
/// </summary>
public sealed partial class MainWindow
{
    List<ExtensionState> extensionStates = PgExtensions.Merge([]);
    Guid? extensionSession;              // session the list was read from; null means the offline guide
    Guid? extensionLoadingSession;       // session a read is in flight for
    Guid? extensionFailedSession;        // session whose list could not be read; not retried automatically
    string extensionFailure = "";        // why that read failed
    bool ExtensionListFailed => db != null && extensionFailedSession == db.SessionIdentity;
    string? selectedExtension;
    CancellationTokenSource? extensionDetailLoad;
    Action? renderExtensionList, renderExtensionTiles;
    Func<Task>? reloadExtensions;
    readonly ContentControl extensionDetail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    FrameworkElement BuildExtensions()
    {
        var search = new TextBox { PlaceholderText = "Search extensions…", MinWidth = 220 };
        AutomationProperties.SetName(search, "Search extensions");
        var show = new ComboBox { ItemsSource = new[] { "All", "Installed", "Ready to install", "Not on this server", "Has a visual" }, SelectedIndex = 0, MinWidth = 170 };
        AutomationProperties.SetName(show, "Filter by status");
        var category = new ComboBox { ItemsSource = new[] { "All categories" }.Concat(PgExtensions.Categories).Append("Other").ToArray(), SelectedIndex = 0, MinWidth = 190 };
        AutomationProperties.SetName(category, "Filter by category");
        var source = Muted("", 13);
        var list = BrandControl(new ListView { SelectionMode = ListViewSelectionMode.Single });
        AutomationProperties.SetName(list, "Extensions");

        var installedTile = Tile("Installed", "–", out var setInstalled);
        var readyTile = Tile("Ready to install", "–", out var setReady);
        var updateTile = Tile("Updates", "–", out var setUpdates);
        var visualTile = Tile("With a visual", "–", out var setVisual);

        renderExtensionTiles = () =>
        {
            bool online = extensionSession != null;
            string unknown = ExtensionListFailed ? "list not available" : "connect to see";
            setInstalled(online ? extensionStates.Count(e => e.Installed).ToString() : "–", online ? "in this database" : unknown);
            setReady(online ? extensionStates.Count(e => e.Available && !e.Installed).ToString() : "–", online ? "offered by the server" : unknown);
            setUpdates(online ? extensionStates.Count(e => e.Outdated).ToString() : "–", online ? "newer version on the server" : unknown);
            setVisual(extensionStates.Count(e => e.Guide?.Insight.Length > 0 && (e.Installed || !online)).ToString(), online ? "installed, with a dedicated view" : "in the guide");
            source.Text = online
                ? $"{db!.Profile.Name} · {extensionStates.Count(e => e.Available || e.Installed)} extensions on this server"
                : ExtensionListFailed ? $"The extension list could not be read from {db!.Profile.Name}: {extensionFailure} · showing the built-in guide"
                : "Not connected · showing the built-in guide to widely used extensions";
            // The navigation, Home and the command palette follow what is installed.
            RebuildDatabaseNavigation();
        };

        renderExtensionList = () =>
        {
            bool online = extensionSession != null;
            string text = search.Text.Trim();
            var matches = extensionStates.Where(e =>
                (text.Length == 0 || e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Summary.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(text, StringComparison.OrdinalIgnoreCase))
                && (category.SelectedIndex <= 0 || e.Category == (string)category.SelectedItem)
                && show.SelectedIndex switch { 1 => e.Installed, 2 => e.Available && !e.Installed, 3 => !e.Available && !e.Installed, 4 => e.Guide?.Insight.Length > 0, _ => true }).ToList();
            list.Items.Clear();
            foreach (var state in matches) list.Items.Add(ExtensionListItem(state, online));
            if (matches.Count == 0) { extensionDetail.Content = EmptyState("02-wait-what", "Nothing matches", "No extension fits this search and filter. Clear them to see everything."); return; }
            var keep = list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ExtensionState)i.Tag).Name == selectedExtension);
            list.SelectedItem = keep ?? list.Items[0];
        };

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListViewItem { Tag: ExtensionState state }) return;
            selectedExtension = state.Name; ShowExtension(state);
        };
        search.TextChanged += (_, _) => renderExtensionList();
        // The read-only note in the detail follows the checkbox.
        void ReadOnlyChanged() { if (currentRoute == "Extensions" && list.SelectedItem is ListViewItem { Tag: ExtensionState state }) ShowExtension(state); }
        readOnly.Checked += (_, _) => ReadOnlyChanged(); readOnly.Unchecked += (_, _) => ReadOnlyChanged();
        show.SelectionChanged += (_, _) => renderExtensionList();
        category.SelectionChanged += (_, _) => renderExtensionList();

        var refresh = Button("Refresh", async (_, _) => await reloadExtensions!());
        ToolTipService.SetToolTip(refresh, "Read the extension list from the server again");
        reloadExtensions = async () =>
        {
            var active = db;
            if (active == null) { extensionStates = PgExtensions.Merge([]); extensionSession = null; renderExtensionTiles(); renderExtensionList(); return; }
            refresh.IsEnabled = false; source.Text = "Reading extensions from " + active.Profile.Name + "…";
            extensionLoadingSession = active.SessionIdentity;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var states = await active.Extensions(timeout.Token);
                if (!ReferenceEquals(active, db)) return;   // the connection changed while this was in flight
                extensionStates = states; extensionSession = active.SessionIdentity; extensionFailedSession = null;
                renderExtensionTiles(); renderExtensionList();
            }
            catch (Exception error)
            {
                if (!ReferenceEquals(active, db)) return;
                // Without the list no extension page is offered; the Extensions page's Refresh tries again.
                extensionStates = PgExtensions.Merge([]); extensionSession = null; extensionFailedSession = active.SessionIdentity; extensionFailure = Describe(error);
                renderExtensionTiles(); renderExtensionList();
            }
            finally { refresh.IsEnabled = true; if (extensionLoadingSession == active.SessionIdentity) extensionLoadingSession = null; }
        };

        var title = Display("Extensions", 30);
        var intro = Muted("See what your Postgres server offers, install what you need, and look inside the extensions already at work.", 14);
        var filters = ScrollTools(Row(search, show, category, refresh));
        var tiles = Flow(170, installedTile, readyTile, updateTile, visualTile);

        var listCard = Surface(list, 6);
        var detailCard = Surface(new ScrollViewer { Content = extensionDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 0) }, 22);
        var body = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        body.Children.Add(listCard); body.Children.Add(detailCard);
        // Side by side when there is room; stacked with a shorter list when the window is narrow.
        void Arrange()
        {
            bool narrow = body.ActualWidth < 900;
            body.ColumnDefinitions.Clear(); body.RowDefinitions.Clear();
            if (narrow)
            {
                body.RowDefinitions.Add(new() { Height = new GridLength(240) }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(detailCard, 0); Grid.SetRow(detailCard, 1);
            }
            else
            {
                body.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Clamp(body.ActualWidth * .36, 340, 460)) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(detailCard, 1); Grid.SetRow(detailCard, 0);
            }
        }
        body.SizeChanged += (_, _) => Arrange(); Arrange();

        var page = new Grid { RowSpacing = 14 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) page.RowDefinitions.Add(new() { Height = height });
        var heading = Stack(title, intro, source); heading.Spacing = 4;
        page.Children.Add(heading);
        Grid.SetRow(tiles, 1); page.Children.Add(tiles);
        Grid.SetRow(filters, 2); page.Children.Add(filters);
        Grid.SetRow(body, 3); page.Children.Add(body);
        renderExtensionTiles(); renderExtensionList();
        return page;
    }

    /// <summary>
    /// Called whenever connection state may have changed. The list is read once per session as soon as the
    /// session is ready, whether or not the Extensions page is open, because the navigation depends on it.
    /// It runs beside the connection work and never blocks it.
    /// </summary>
    void ExtensionsConnectionChanged()
    {
        if (reloadExtensions == null || connectingProfile != null) return;   // wait until a new session is fully prepared
        var session = db?.SessionIdentity;
        if (session == extensionSession) return;
        if (session != null && (session == extensionLoadingSession || session == extensionFailedSession)) return;
        _ = reloadExtensions();
    }

    static string Describe(Exception error) => error is Npgsql.PostgresException pg ? pg.MessageText + (pg.Hint == null ? "" : " " + pg.Hint) : error.Message;

    static (string Label, Tone Tone) ExtensionStatus(ExtensionState state, bool online) =>
        !online ? ("Guide", Tone.Neutral)
        : state.Outdated ? ($"Installed {state.InstalledVersion} · {state.DefaultVersion} available", Tone.Accent)
        : state.Installed ? ("Installed " + state.InstalledVersion, Tone.Good)
        : state.Available ? ("Ready to install", Tone.Accent)
        : ("Not on this server", Tone.Neutral);

    ListViewItem ExtensionListItem(ExtensionState state, bool online)
    {
        var name = Text(state.Title, 14); name.FontSize = 14; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var summary = Muted(state.Summary, 12); summary.MaxLines = 2; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var (label, tone) = ExtensionStatus(state, online);
        var status = Pill(state.Outdated ? "Update available" : label, tone);
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(name); Grid.SetColumn(status, 1); top.Children.Add(status);
        var content = new StackPanel { Spacing = 3, Padding = new Thickness(4, 9, 4, 9) };
        content.Children.Add(top); content.Children.Add(summary);
        // Extensions the server does not have recede so the actionable ones lead.
        if (online && !state.Available && !state.Installed) content.Opacity = .62;
        var item = new ListViewItem { Content = content, Tag = state, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, state.Title + ", " + label);
        return item;
    }

    static StackPanel Section(string title, FrameworkElement body, string caption = "")
    {
        var section = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 0) };
        var heading = Display(title, 17); section.Children.Add(heading);
        if (caption.Length > 0) section.Children.Add(Muted(caption, 12));
        section.Children.Add(body);
        return section;
    }

    void OpenSqlDraft(string title, string sql)
    {
        Navigate("Query"); AddQuery(new(Guid.NewGuid(), title, sql));
        status.Text = "Draft opened in SQL · nothing has run";
    }

    void ShowExtension(ExtensionState state)
    {
        extensionDetailLoad?.Cancel(); extensionDetailLoad?.Dispose(); extensionDetailLoad = new();
        var ct = extensionDetailLoad.Token;
        // The detail eases in for each extension chosen, so the change of subject reads at a glance.
        Fluent.Appear(extensionDetail, 6);
        bool online = extensionSession != null && db != null;
        var (label, tone) = ExtensionStatus(state, online);

        var title = Display(state.Title, 26);
        var identity = Muted(state.Title == state.Name ? state.Category : state.Name + " · " + state.Category, 13);
        var pills = Row(Pill(label, tone));
        if (state.Guide?.Insight.Length > 0) pills.Children.Add(Pill("Has a visual", Tone.Good));
        if (state.Guide?.Preload == true) pills.Children.Add(Pill("Needs preload", Tone.Neutral));
        if (state.Requires.Length > 0) pills.Children.Add(Pill("Requires " + string.Join(", ", state.Requires)));
        var summary = Text(state.Summary, 14); summary.FontSize = 14;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title); panel.Children.Add(identity); panel.Children.Add(ScrollTools(pills)); panel.Children.Add(summary);
        if (state.Guide != null && state.Comment.Length > 0 && state.Comment != state.Summary) panel.Children.Add(Muted("Server description · " + state.Comment, 12));

        // Actions. Every change is confirmed first and shows the exact statement.
        var actions = Row();
        if (online && state.Available && !state.Installed) actions.Children.Add(Button("Install", async (_, _) => await ChangeExtension(state, "install"), true));
        if (online && state.Outdated) actions.Children.Add(Button("Update to " + state.DefaultVersion, async (_, _) => await ChangeExtension(state, "update"), true));
        // An installed extension with a dedicated page gets the way into it first.
        if (online && state.Installed && PgExtensions.PageKey(state) is { } pageKey && PgExtensions.Pages.FirstOrDefault(p => p.Key == pageKey) is { } pageInfo)
            actions.Children.Insert(0, Button("Open " + pageInfo.Label, (_, _) => Navigate(ExtensionRoute(pageKey)), !state.Outdated));
        if (online && state.Installed && state.Name != "plpgsql") actions.Children.Add(Button("Remove", async (_, _) => await ChangeExtension(state, "remove")));
        if (state.Guide != null)
        {
            var docs = BrandControl(new HyperlinkButton { Content = "Documentation", NavigateUri = new Uri(state.Guide.Docs), Padding = new Thickness(8, 6, 8, 6) });
            actions.Children.Add(docs);
        }
        if (actions.Children.Count > 0) panel.Children.Add(ScrollTools(actions));

        if (ExtensionListFailed)
        {
            // Connected, but the list behind every status here could not be read.
            panel.Children.Add(Muted($"The extension list could not be read from {db!.Profile.Name} ({extensionFailure}), so whether this server offers it is unknown.", 13));
            panel.Children.Add(Row(Button("Try again", async (_, _) => { if (reloadExtensions != null) await reloadExtensions(); }, true)));
        }
        else if (!online)
            panel.Children.Add(Muted("Connect to a database to see whether this server offers it, install it, and inspect what it is doing.", 13));
        else if (!state.Available && !state.Installed)
            panel.Children.Add(Muted("This server does not ship the extension, so it cannot be installed from here. It has to be added to the Postgres installation (or enabled by your hosting provider) first.", 13));
        else if (readOnly.IsChecked == true && (!state.Installed || state.Name != "plpgsql"))
            panel.Children.Add(Muted("Read-only is on for this session. Turn it off in SQL before installing, updating or removing an extension.", 12));
        if (state.Guide?.Preload == true && online && !state.Installed && state.Available)
            panel.Children.Add(Muted($"{state.Title} loads at server start. Add it to shared_preload_libraries and restart Postgres, otherwise it installs but stays inactive.", 12));

        if (online && state.Installed)
        {
            var facts = new[] { new[] { state.InstalledVersion ?? "", state.DefaultVersion ?? "–", state.Schema ?? "–" } };
            panel.Children.Add(Section("Installed here", MiniTable(["Version", "Server default", "Schema"], facts)));

            var insightHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            if (state.Guide?.Insight.Length > 0)
            {
                insightHost.Content = Muted("Reading from the database…", 13);
                panel.Children.Add(insightHost);
            }
            var footprintHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Muted("Reading from the database…", 13) };
            panel.Children.Add(Section("Footprint", footprintHost, "What this extension added to the database and which of your tables rely on it."));
            var settingsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(settingsHost);
            _ = LoadExtensionDetail(state, db!, insightHost, footprintHost, settingsHost, ct);
        }
        else if (!online && !ExtensionListFailed && state.Guide?.Insight.Length > 0)
            panel.Children.Add(Section("When installed", Muted(InsightPromise(state.Guide.Insight), 13)));

        extensionDetail.Content = panel;
    }

    static string InsightPromise(string insight) => insight switch
    {
        "vector" => "DB Cowboy lists every vector column with its dimensions, shows which have an HNSW or IVFFlat index and how large each index is, and drafts an index for columns that lack one.",
        "statements" => "DB Cowboy charts the statements that consumed the most execution time, with calls, mean time and cache hit rate, and opens any of them in SQL.",
        "postgis" => "DB Cowboy lists geometry and geography columns with their type and SRID, and plots a sample of any column.",
        "timescaledb" => "DB Cowboy charts hypertables by chunk count and shows compression and background jobs.",
        "cron" => "DB Cowboy shows each scheduled job with a strip of its most recent runs so failures stand out.",
        "trgm" => "DB Cowboy lets you compare two strings and see their shared trigrams and similarity scores.",
        "buffercache" => "DB Cowboy charts which relations occupy the shared buffer cache.",
        "fdw" => "DB Cowboy lists the foreign servers and foreign tables defined through this wrapper.",
        "partman" => "DB Cowboy lists the partition sets pg_partman maintains.",
        "pgmq" => "DB Cowboy charts queue depth and message age for every queue.",
        "citus" => "DB Cowboy lists worker nodes and distributed tables.",
        "age" => "DB Cowboy lists graphs with their vertex and edge labels.",
        _ => ""
    };

    async Task LoadExtensionDetail(ExtensionState state, Db active, ContentControl insightHost, ContentControl footprintHost, ContentControl settingsHost, CancellationToken ct)
    {
        // Each part loads and fails on its own, so one missing privilege does not blank the whole panel.
        if (state.Guide?.Insight.Length > 0)
        {
            try { var view = await ExtensionInsight(state, active, ct); if (!ct.IsCancellationRequested) insightHost.Content = view; }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { if (!ct.IsCancellationRequested) insightHost.Content = Section("Live view", Muted("This view could not be read: " + Describe(error), 13)); }
        }
        try
        {
            var footprint = await active.ExtensionFootprint(state.Name, ct);
            if (ct.IsCancellationRequested) return;
            var parts = new StackPanel { Spacing = 12 };
            var counts = new List<string>();
            void Count(int n, string one, string many) { if (n > 0) counts.Add(n + " " + (n == 1 ? one : many)); }
            Count(footprint.Functions, "function", "functions"); Count(footprint.Types, "type", "types"); Count(footprint.Operators, "operator", "operators");
            Count(footprint.AccessMethods, "index method", "index methods"); Count(footprint.Tables, "table or view", "tables and views");
            parts.Children.Add(Text(counts.Count == 0 ? "No catalog objects are recorded for this extension." : "Provides " + string.Join(" · ", counts), 13));
            if (footprint.Columns.Count > 0)
                parts.Children.Add(Stack(Eyebrow($"Columns using its types · {footprint.Columns.Count}{(footprint.Columns.Count == 200 ? "+" : "")}"), MiniTable(["Table", "Column", "Type"], footprint.Columns.Take(25).Select(u => new[] { u.Relation, u.Object, u.Detail }))));
            if (footprint.Indexes.Count > 0)
                parts.Children.Add(Stack(Eyebrow($"Indexes using it · {footprint.Indexes.Count}{(footprint.Indexes.Count == 200 ? "+" : "")}"), MiniTable(["Table", "Index", "Method · size"], footprint.Indexes.Take(25).Select(u => new[] { u.Relation, u.Object, u.Detail }))));
            if (footprint.Columns.Count == 0 && footprint.Indexes.Count == 0)
                parts.Children.Add(Muted("No table column or index in this database depends on it yet.", 13));
            footprintHost.Content = parts;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { if (!ct.IsCancellationRequested) footprintHost.Content = Muted("The footprint could not be read: " + Describe(error), 13); }
        try
        {
            var settings = await active.ExtensionSettings(state.Name, ct);
            if (ct.IsCancellationRequested || settings.Count == 0) return;
            var table = MiniTable(["Parameter", "Value", "Meaning"], settings.Select(s => new[] { s.Name, s.Value + (s.Unit.Length > 0 ? " " + s.Unit : ""), s.Description }), [1.4, .8, 2.2]);
            settingsHost.Content = Section("Settings", table, "Current values for this session. Change them with SET, ALTER DATABASE or the server configuration.");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Settings are supplementary; the rest of the panel already explains the extension. */ }
    }

    async Task ChangeExtension(ExtensionState state, string verb)
    {
        var active = db; if (active == null) return;
        if (readOnly.IsChecked == true) { await Notice("Read-only is on", "This session is read-only. Turn off Read-only in SQL, then try again."); return; }
        // Extensions this one needs that are not installed yet; CASCADE installs them in the same statement.
        var missing = verb == "install" ? state.Requires.Where(r => extensionStates.FirstOrDefault(e => e.Name == r)?.Installed != true).ToArray() : [];
        string sql = verb switch { "install" => Db.InstallExtensionSql(state.Name, missing.Length > 0), "update" => $"ALTER EXTENSION {Db.Q(state.Name)} UPDATE", _ => $"DROP EXTENSION {Db.Q(state.Name)}" };
        string consequence = verb switch
        {
            "install" => "This adds the extension's types, functions and operators to the current database." + (missing.Length > 0 ? "\n\nIt needs " + string.Join(", ", missing) + ", which will be installed with it." : ""),
            "update" => $"This moves the extension from {state.InstalledVersion} to {state.DefaultVersion} by running its update scripts.",
            _ => "This removes the extension's types, functions and operators. It does not cascade: if any column, index or view depends on it, Postgres refuses and nothing changes."
        };
        string action = verb switch { "install" => "Install", "update" => "Update", _ => "Remove" };
        if (!await Confirm($"{action} {state.Title} in {active.Profile.Database}?", $"{consequence}{ProductionNote(active)}\n\nStatement:\n{sql}", action)) return;
        bool done = false;
        await Work(async ct =>
        {
            if (verb == "install") await active.InstallExtension(state.Name, missing.Length > 0, ct); else if (verb == "update") await active.UpdateExtension(state.Name, ct); else await active.DropExtension(state.Name, ct);
            done = true;
        });
        if (done) status.Text = verb switch { "install" => state.Title + " installed", "update" => state.Title + " updated", _ => state.Title + " removed" } + " · " + active.Profile.Database;
        if (reloadExtensions != null) await reloadExtensions();
    }
}
