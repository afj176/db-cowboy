using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DbCowboy;

/// <summary>
/// Pages that exist only while the connected database has the extension behind them: Vector lab for pgvector,
/// and one live view per other extension with a dedicated visual. They appear in the navigation under
/// "In this database", on Home and in the command palette, and go away with the extension or the connection.
/// </summary>
public sealed partial class MainWindow
{
    const string ExtensionViewPage = "Extension view", ExtensionRoutePrefix = "ext:", VectorLabRoute = "Vector lab";

    readonly List<object> databaseNavItems = [];   // the header and items this file added to the navigation
    string? currentExtensionKey;                   // which extension page "Extension view" is showing
    string? pendingRoute;                          // a route asked for before this session's extension list arrived
    Action<string>? showExtensionPage;
    CancellationTokenSource? extensionPageLoad;

    static string ExtensionRoute(string key) => key == "vector" ? VectorLabRoute : ExtensionRoutePrefix + key;

    static Symbol ExtensionIcon(string key) => key switch
    {
        "vector" => Symbol.World, "statements" => Symbol.ThreeBars, "postgis" => Symbol.Map, "timescaledb" => Symbol.Library,
        "cron" => Symbol.Calendar, "trgm" => Symbol.Font, "buffercache" => Symbol.Bullets, "fdw" => Symbol.Link,
        "partman" => Symbol.Folder, "pgmq" => Symbol.Mail, "citus" => Symbol.Globe, "age" => Symbol.Shuffle,
        _ => Symbol.AllApps
    };

    /// <summary>The extension list belongs to the live session only once it has been read from that session.</summary>
    bool ExtensionListIsCurrent => db != null && extensionSession == db.SessionIdentity;

    /// <summary>The extension pages that apply to the connected database. Empty when disconnected or the list could not be read.</summary>
    List<(ExtensionPageInfo Page, List<ExtensionState> Extensions)> AvailableExtensionPages() =>
        ExtensionListIsCurrent ? PgExtensions.PagesFor(extensionStates) : [];

    bool RouteAvailable(string route) =>
        route != VectorLabRoute && !route.StartsWith(ExtensionRoutePrefix) || AvailableExtensionPages().Any(p => ExtensionRoute(p.Page.Key) == route);

    /// <summary>
    /// Rebuilds the "In this database" group from the current extension list, then makes sure the page on screen
    /// still exists. Called on connect, disconnect and after an extension is installed, updated or removed.
    /// </summary>
    void RebuildDatabaseNavigation()
    {
        foreach (var item in databaseNavItems) nav.MenuItems.Remove(item);
        databaseNavItems.Clear();
        var available = AvailableExtensionPages();
        if (available.Count > 0)
        {
            // No header without items: an empty group would only raise a question.
            databaseNavItems.Add(new NavigationViewItemHeader { Content = "IN THIS DATABASE" });
            foreach (var (page, extensions) in available)
            {
                var item = new NavigationViewItem { Content = page.Label, Tag = ExtensionRoute(page.Key), Icon = new SymbolIcon(ExtensionIcon(page.Key)), Height = 36, MinHeight = 36, FontSize = 15 };
                // Vector lab already knows how many columns it found; the other pages read their numbers when opened.
                string hint = page.Key == "vector" && vectorColumn.ItemsSource is IEnumerable<VectorColumn> columns ? $"\n{columns.Count()} vector {(columns.Count() == 1 ? "column" : "columns")} found." : "";
                ToolTipService.SetToolTip(item, page.Description + "\nFrom " + string.Join(", ", extensions.Select(e => e.Title + " " + e.InstalledVersion)) + "." + hint);
                databaseNavItems.Add(item);
            }
            foreach (var item in databaseNavItems) nav.MenuItems.Add(item);
        }

        // A route asked for while the list was still loading (restoring the last page after connect).
        if (pendingRoute is { } wanted && ExtensionListIsCurrent)
        {
            pendingRoute = null;
            if (RouteAvailable(wanted)) { Navigate(wanted); refreshHome?.Invoke(); return; }
        }

        string onScreen = currentRoute == ExtensionViewPage && currentExtensionKey != null ? ExtensionRoute(currentExtensionKey) : currentRoute;
        if (!RouteAvailable(onScreen))
        {
            // The page's extension or connection is gone. While a new session's list is still loading, wait for it.
            bool stillLoading = db != null && !ExtensionListIsCurrent && extensionFailedSession != db.SessionIdentity;
            if (!stillLoading) Navigate(db != null ? "Extensions" : "Home");
        }
        else if (nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, onScreen)) is { } selected)
            nav.SelectedItem = selected;   // the item object was recreated above
        refreshHome?.Invoke();
    }

    /// <summary>One page hosts every extension view; it is filled for the extension that was opened.</summary>
    FrameworkElement BuildExtensionViewPage()
    {
        var title = Display("", 30);
        var description = Muted("", 14);
        var refresh = ToolButton(Symbol.Refresh, "Refresh", async (_, _) =>
        {
            // Re-read the extension list first: if the extension was removed outside the app, the navigation
            // drops this page and moves away instead of showing a view that can no longer be read.
            string? key = currentExtensionKey;
            if (reloadExtensions != null) await reloadExtensions();
            if (key != null && key == currentExtensionKey && AvailableExtensionPages().Any(p => p.Page.Key == key)) showExtensionPage?.Invoke(key);
        }, "Read this view from the database again");
        AutomationProperties.SetName(refresh, "Refresh extension view");
        var details = ToolButton(Symbol.AllApps, "Extension details", (_, _) =>
        {
            // Open the Extensions page on the extension behind this view.
            var owner = AvailableExtensionPages().FirstOrDefault(p => p.Page.Key == currentExtensionKey).Extensions?.FirstOrDefault();
            if (owner != null) selectedExtension = owner.Name;
            Navigate("Extensions"); renderExtensionList?.Invoke();
        }, "Version, footprint and settings on the Extensions page");
        var header = PageHeader(title, description, ScrollTools(Row(Pill("Read-only"), refresh, details)));

        var body = new StackPanel { Spacing = 14 };
        var scroll = new ScrollViewer { Content = LeftCapped(body, 1180), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 12) };

        async Task Load(string key)
        {
            // Cancelling ends the previous load; its token stays readable after the source is disposed.
            extensionPageLoad?.Cancel(); extensionPageLoad?.Dispose(); extensionPageLoad = new();
            var ct = extensionPageLoad.Token;
            var entry = AvailableExtensionPages().FirstOrDefault(p => p.Page.Key == key);
            var active = db;
            // RebuildDatabaseNavigation moves off a page that no longer applies; a cancelled earlier load may have left Refresh off.
            if (entry.Page == null || active == null) { refresh.IsEnabled = true; return; }
            title.Text = entry.Page.Label;
            description.Text = entry.Page.Description + " From " + string.Join(", ", entry.Extensions.Select(e => e.Title)) + " in " + active.Profile.Database + ".";
            body.Children.Clear();
            refresh.IsEnabled = false;
            try
            {
                foreach (var extension in entry.Extensions)
                {
                    // Each extension's view loads and fails on its own.
                    var host = Fluent.Swap(new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Fluent.Skeleton(.35, .95, .9, .7, .85) });
                    body.Children.Add(Surface(host, 22));
                    try
                    {
                        var view = await ExtensionInsight(extension, active, ct);
                        if (ct.IsCancellationRequested) return;
                        // Sections carry a top margin for the Extensions detail panel; the card already provides the space.
                        if (view is StackPanel section) section.Margin = new Thickness(0);
                        host.Content = view;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error)
                    {
                        if (ct.IsCancellationRequested) return;
                        // A missing privilege is the common case for a restricted role; say whose and what to ask for.
                        string reason = Describe(error) + (error is Npgsql.PostgresException { SqlState: "42501" }
                            ? $"\n\nThe role {active.Profile.Username} lacks a privilege this view needs. A database owner or administrator can grant it (for example GRANT USAGE ON SCHEMA … and GRANT SELECT ON the extension's tables), or connect with a role that has it. Other pages are unaffected."
                            : "");
                        host.Content = EmptyState("03-im-done", extension.Title + " could not be read", reason, Button("Try again", (_, _) => showExtensionPage?.Invoke(key), true));
                    }
                }
            }
            finally { if (!ct.IsCancellationRequested) refresh.IsEnabled = true; }
        }

        showExtensionPage = key => { currentExtensionKey = key; scroll.ChangeView(0, 0, 1, true); _ = Load(key); };

        var page = new Grid { RowSpacing = 12 };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(header);
        Grid.SetRow(scroll, 1); page.Children.Add(scroll);
        return page;
    }
}
