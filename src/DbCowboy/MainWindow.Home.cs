using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

/// <summary>Home: where you are connected, what is in that database, and the quickest way into each workspace.</summary>
public sealed partial class MainWindow
{
    Action? refreshHome;
    Guid? homeOverviewSession;

    /// <summary>A whole card that acts as one button, with hover and keyboard focus from the native Button.</summary>
    static Button CardButton(Symbol icon, string title, string detail, Action open)
    {
        var glyph = new SymbolIcon(icon) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        var heading = Display(title, 17); var body = Muted(detail, 13);
        var text = new StackPanel { Spacing = 4 }; text.Children.Add(heading); text.Children.Add(body);
        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(glyph); Grid.SetColumn(text, 1); layout.Children.Add(text);
        var button = Fluent.Lift(new Button { Content = layout, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(18, 16, 18, 18), CornerRadius = new CornerRadius(12), MinHeight = 96 });
        // The card hands off to the page it opens.
        button.Click += (_, _) => { Fluent.Prepare("workspace-card", button); open(); };
        AutomationProperties.SetName(button, title + ". " + detail);
        Themed(button, (b, dark) =>
        {
            if (HighContrast) { glyph.ClearValue(IconElement.ForegroundProperty); return; }
            glyph.Foreground = new SolidColorBrush(ToneColor(Tone.Accent, dark));
            b.Background = new SolidColorBrush(dark ? Brand.Raised : Microsoft.UI.Colors.White);
        });
        return button;
    }

    FrameworkElement BrandHome()
    {
        var hero = new Image { Height = 148, Width = 148, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        Themed(hero, (image, dark) => image.Source = Asset(dark ? "rowdy-hero-dark.png" : "rowdy-hero.png"));
        AutomationProperties.SetName(hero, "Rowdy, the DB Cowboy mascot, tipping his copper hat");

        var title = Display("Your data. Your frontier.", 36);
        var stateHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var intro = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Eyebrow("Postgres workspace")); intro.Children.Add(title); intro.Children.Add(stateHost);
        var heroRow = new Grid { ColumnSpacing = 24 };
        heroRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heroRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heroRow.Children.Add(hero); Grid.SetColumn(intro, 1); heroRow.Children.Add(intro);
        // The hero sits on a frontier horizon that drifts with the scroll (see MainWindow.HomeArt.cs).
        var homeScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        heroRow.Padding = new Thickness(28, 26, 28, 30);
        var heroCard = HomeBanner(homeScroll, heroRow);
        // On a narrow window the mascot yields its space to the text.
        heroCard.SizeChanged += (_, _) => { bool narrow = heroCard.ActualWidth < 620; hero.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible; title.FontSize = narrow ? 28 : 36; };

        var tables = Tile("Tables", "–", out var setTables);
        var vectors = Tile("Vector columns", "–", out var setVectors);
        var extensions = Tile("Extensions", "–", out var setExtensions);
        var size = Tile("Database size", "–", out var setSize);
        var overview = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        int vectorColumns = 0; bool? showingVectors = null;
        // The vector tile belongs to databases with a vector extension, like the Vector lab page.
        void ArrangeOverview()
        {
            bool withVectors = vectorColumns > 0 || ExtensionListIsCurrent && extensionStates.Any(e => e.Installed && PgExtensions.PageKey(e) == "vector");
            if (showingVectors == withVectors) return;
            showingVectors = withVectors;
            (overview.Content as Panel)?.Children.Clear();   // release the tiles before they join the new layout
            overview.Content = withVectors ? Flow(170, tables, vectors, extensions, size) : Flow(170, tables, extensions, size);
        }
        ArrangeOverview();

        // One card per page in the navigation, in the same order. Extension pages appear only while they apply.
        var workspacesHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void RenderWorkspaces()
        {
            var cards = new List<FrameworkElement>
            {
                CardButton(Symbol.Edit, "SQL", "Write queries, read plans, chart results and keep your tabs.", () => Navigate("Query")),
                CardButton(Symbol.List, "Tables", "Browse rows, structure and relationships. Stage edits, then review and apply.", () => Navigate("Table")),
                CardButton(Symbol.AllApps, "Extensions", "See what the server offers, install what you need and look inside what is running.", () => Navigate("Extensions"))
            };
            foreach (var (extensionPage, _) in AvailableExtensionPages())
                cards.Add(CardButton(ExtensionIcon(extensionPage.Key), extensionPage.Label, extensionPage.Description, () => Navigate(ExtensionRoute(extensionPage.Key))));
            cards.Add(CardButton(Symbol.Clock, "Activity", "See who is connected right now and reopen queries you have run.", () => Navigate("Activity")));
            cards.Add(CardButton(Symbol.Contact, "Connections", "Save, favorite and switch between your Postgres databases.", () => Navigate("Connections")));
            // Cards rise in one after another, glide when the grid reflows, and arrow keys move between them.
            workspacesHost.Content = Fluent.ArrowKeys(Fluent.Stagger(Flow(250, cards.ToArray())));
        }

        var recentConnections = new StackPanel { Spacing = 6 };
        var recentQueries = new StackPanel { Spacing = 6 };
        var recent = Flow(360, Surface(Stack(Display("Recent connections", 17), recentConnections), 18), Surface(Stack(Display("Recent queries", 17), recentQueries), 18));

        Button RowButton(string primary, string secondary, Action open, string name)
        {
            var first = Text(primary, 13); first.FontSize = 13; first.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; first.TextWrapping = TextWrapping.NoWrap; first.TextTrimming = TextTrimming.CharacterEllipsis;
            var second = Muted(secondary, 12); second.TextWrapping = TextWrapping.NoWrap; second.TextTrimming = TextTrimming.CharacterEllipsis;
            var content = new StackPanel { Spacing = 1 }; content.Children.Add(first); content.Children.Add(second);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 7, 10, 8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            button.Click += (_, _) => open(); AutomationProperties.SetName(button, name);
            return button;
        }

        void RenderState()
        {
            var active = db; var selected = connections.SelectedItem as Profile;
            var panel = new StackPanel { Spacing = 12 };
            if (active != null)
            {
                panel.Children.Add(Text($"{active.Profile.Host}:{active.Profile.Port} / {active.Profile.Database}", 15));
                var pills = Row(Pill("Connected · " + active.Profile.Name, Tone.Good));
                if (active.Profile.Production) pills.Children.Add(Pill("Production", Tone.Warn));
                if (readOnly.IsChecked == true) pills.Children.Add(Pill("Read-only"));
                panel.Children.Add(ScrollTools(pills));
                panel.Children.Add(ScrollTools(Row(Button("Open SQL", (_, _) => Navigate("Query"), true), Button("Browse tables", (_, _) => Navigate("Table")), Button("Switch connection", ConnectionsClick))));
            }
            else if (profiles.Count == 0 && Store.IsUnreadable(Store.ConnectionsFile))
                panel.Children.Add(Text("Your saved connections could not be read. The notice at the bottom of the window says where the file and its copy are.", 15));
            else if (profiles.Count == 0)
            {
                panel.Children.Add(Text("Add your first Postgres connection to start working with SQL, tables and vectors.", 15));
                panel.Children.Add(Row(Button("New connection", NewConnectionClick, true)));
            }
            else
            {
                panel.Children.Add(Text("Pick up where you left off, or open another database.", 15));
                var actions = Row();
                if (selected != null) actions.Children.Add(Button("Connect to " + selected.Name, ConnectClick, true));
                actions.Children.Add(Button("All connections", ConnectionsClick)); actions.Children.Add(Button("New connection", NewConnectionClick));
                panel.Children.Add(ScrollTools(actions));
            }
            stateHost.Content = panel;
        }

        void RenderRecent()
        {
            recentConnections.Children.Clear();
            foreach (var profile in profiles.Where(p => p.LastConnected != null).OrderByDescending(p => p.LastConnected).Take(4))
                recentConnections.Children.Add(RowButton(profile.Name + (profile.Production ? "  ·  production" : ""), $"{profile.Endpoint} · last opened {profile.LastConnected!.Value.LocalDateTime:g}", async () => await ConnectProfile(profile), "Connect to " + profile.Name));
            if (recentConnections.Children.Count == 0) recentConnections.Children.Add(Muted(profiles.Count == 0 ? "No saved connections yet." : "Connections you open will appear here.", 13));
            recentQueries.Children.Clear();
            foreach (var entry in history.OrderByDescending(q => q.At).Take(5))
            {
                string sql = string.Join(" ", entry.Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                recentQueries.Children.Add(RowButton(sql.Length > 90 ? sql[..90] + "…" : sql, $"{entry.Connection} · {entry.At:g} · {entry.Rows:N0} {(entry.Rows == 1 ? "row" : "rows")} · {entry.Milliseconds:N0} ms", () => OpenSqlDraft("From history", entry.Sql), "Open query from history"));
            }
            if (recentQueries.Children.Count == 0) recentQueries.Children.Add(Muted("Queries you run are kept here, on this computer.", 13));
        }

        async Task LoadOverview()
        {
            var active = db;
            if (active == null) { overview.Visibility = Visibility.Collapsed; homeOverviewSession = null; vectorColumns = 0; return; }
            if (homeOverviewSession == active.SessionIdentity) return;
            homeOverviewSession = active.SessionIdentity; vectorColumns = 0;
            // Skeleton tiles hold the place while Postgres answers.
            showingVectors = null; (overview.Content as Panel)?.Children.Clear(); overview.Content = Flow(170, Enumerable.Range(0, 4).Select(_ => (FrameworkElement)Surface(Fluent.SkeletonValue(), 16)).ToArray()); overview.Visibility = Visibility.Visible;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var facts = await active.Overview(timeout.Token);
                if (!ReferenceEquals(active, db)) return;
                setTables(facts.Tables.ToString("N0"), "in " + facts.Database); setVectors(facts.VectorColumns.ToString("N0"), facts.VectorColumns == 0 ? "none yet" : "ready to explore");
                vectorColumns = facts.VectorColumns; showingVectors = null; ArrangeOverview();
                setExtensions(facts.Extensions.ToString("N0"), "installed"); setSize(facts.Size, "PostgreSQL " + facts.Version.Split(' ')[0]);
                overview.Visibility = Visibility.Visible;
            }
            // The overview is a convenience; a role without these privileges still gets a working Home.
            catch { if (ReferenceEquals(active, db)) { overview.Visibility = Visibility.Collapsed; showingVectors = null; ArrangeOverview(); } }
        }

        // What Postgres itself records about this database: sizes, index use and installed extensions.
        var insightsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        DatabaseInsights? insights = null;
        Guid? insightsSession = null;

        void RenderInsights()
        {
            var active = db;
            if (active == null || insightsSession != active.SessionIdentity) { insightsHost.Visibility = Visibility.Collapsed; return; }
            if (insights == null)
            {
                // Loading: three skeleton cards in the shape of the summaries to come.
                insightsHost.Content = Stack(Eyebrow("In this database"), Flow(330, Enumerable.Range(0, 3).Select(_ => (FrameworkElement)Surface(Fluent.Skeleton(.5, .95, .8, .9, .6), 18)).ToArray()));
                insightsHost.Visibility = Visibility.Visible; return;
            }
            var cards = new List<FrameworkElement>();

            FrameworkElement tablesBody = insights.LargestTables.Count == 0
                ? Muted("No tables are visible to this role yet.", 13)
                : Bars(insights.LargestTables.Select(table => Bar(table.Schema + "." + table.Name, table.Bytes, table.Size, $"about {table.Rows:N0} rows",
                    async () => { if (relations.FirstOrDefault(r => r.Schema == table.Schema && r.Name == table.Name) is { } relation) await OpenRelation(relation); })).ToList(), Tone.Good);
            cards.Add(Surface(Stack(Display("Largest tables", 17), Muted("Total size, including indexes and TOAST. Select one to open it.", 12), tablesBody), 18));

            string since = insights.StatsReset == null ? "since statistics were last reset" : "since statistics were reset on " + insights.StatsReset;
            FrameworkElement idleBody = insights.IdleIndexes.Count == 0
                ? Muted("Every non-unique index has been scanned " + since + ".", 13)
                : MiniTable(["Index", "Table", "Size"], insights.IdleIndexes.Select(index => new[] { index.Index, index.Schema + "." + index.Table, index.Size }), [1.4, 1.2, .6]);
            cards.Add(Surface(Stack(Display("Indexes never scanned", 17), Muted("Non-unique indexes with zero scans " + since + ". A new or rarely used index also shows up here, so check before dropping one.", 12), idleBody), 18));

            if (ExtensionListIsCurrent)
            {
                var installed = extensionStates.Where(e => e.Installed).ToList();
                var manage = Button("Manage extensions", (_, _) => Navigate("Extensions"));
                cards.Add(Surface(Stack(Display("Installed extensions", 17), Muted($"{installed.Count} in this database. Those with their own page are in the navigation.", 12),
                    MiniTable(["Extension", "Version", "Page"], installed.Take(8).Select(e => new[] { e.Title, e.InstalledVersion ?? "", PgExtensions.PageKey(e) is { } key ? PgExtensions.Pages.First(p => p.Key == key).Label : "–" }), [1.4, .7, 1]),
                    manage), 18));
            }
            insightsHost.Content = Stack(Eyebrow("In this database"), Fluent.Stagger(Flow(330, cards.ToArray())));
            insightsHost.Visibility = Visibility.Visible;
        }

        async Task LoadInsights()
        {
            var active = db;
            if (active == null) { insights = null; insightsSession = null; RenderInsights(); return; }
            if (insightsSession == active.SessionIdentity) { RenderInsights(); return; }
            insightsSession = active.SessionIdentity; insights = null; RenderInsights();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var loaded = await active.Insights(timeout.Token);
                if (!ReferenceEquals(active, db)) return;
                insights = loaded; RenderInsights();
            }
            // These summaries are a convenience; a role without access to the statistics views still gets a working Home.
            catch { }
        }

        refreshHome = () =>
        {
            if (currentRoute != "Home") return;
            RenderState(); RenderWorkspaces(); RenderRecent(); ArrangeOverview(); _ = LoadOverview(); _ = LoadInsights();
        };

        string build = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
#if DEBUG
        // A development build carries the project file's version, not the number of the latest release.
        build += " · development build";
#endif
        var version = Muted("DB Cowboy " + build + " · Built for Postgres", 12);
        var page = new StackPanel { Spacing = 18, MaxWidth = 1180, Margin = new Thickness(8, 8, 20, 24) };
        page.Children.Add(heroCard); page.Children.Add(overview);
        page.Children.Add(Stack(Eyebrow("Workspaces"), workspacesHost));
        page.Children.Add(insightsHost);
        page.Children.Add(recent); page.Children.Add(version);
        RenderState(); RenderWorkspaces(); RenderRecent();
        homeScroll.Content = page;
        return homeScroll;
    }
}
