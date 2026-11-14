using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace DbCowboy;

public sealed partial class MainWindow : Window
{
    readonly Grid root = new() { RequestedTheme = ElementTheme.Default };
    FrameworkElement? connectionExplorer;
    readonly NavigationView nav = new() { IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false, PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 272 };
    readonly TextBlock status = Text("Ready · connect to Postgres", 12);
    readonly InfoBar feedback = new() { IsOpen = false, IsClosable = true };
    readonly InfoBar storageNotice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Warning };
    readonly ComboBox connections = new() { PlaceholderText = "Choose a connection", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    readonly TextBox filter = new() { PlaceholderText = "Find a table…" };
    readonly TreeView tree = new() { SelectionMode = TreeViewSelectionMode.Single };
    readonly TabView queryTabs = InterfaceTabs.Apply(new TabView { IsAddTabButtonVisible = true, TabWidthMode = TabViewWidthMode.SizeToContent, CanDragTabs = true, CanReorderTabs = true, AllowDropTabs = true },TabPresentation.Document);
    readonly List<QueryTab> queries = [];
    QueryTab ActiveQuery => queryTabs.SelectedItem is TabViewItem item ? queries.First(q => q.Item == item) : queries[0];
    SqlEditor editor => ActiveQuery.Editor;
    ResultGrid results => ActiveQuery.Results;
    readonly CheckBox readOnly = new() { Content = "Read-only", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox tableReadOnly = new() { Content = "Read-only", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    Button? applyChangesButton, discardChangesButton;
    readonly ResultGrid tableGrid = new(), columnsGrid = new(), indexesGrid = new(), foreignKeysGrid = new(), activityGrid = new();
    readonly TextBlock pageSummary = Text("Open a table from the explorer", 12), changeSummary = Text("No pending changes", 12);
    readonly RelationshipView relationships = new();
    readonly List<PendingChange> pendingChanges = [];
    List<ColumnInfo> currentColumns = []; List<ForeignKeyInfo> foreignKeys = []; List<Completion> completionCatalog = [];
    readonly ComboBox vectorColumn = new() { PlaceholderText = "Dense vector column", MinWidth = 300 }, metric = new() { ItemsSource = new[] { "Cosine", "L2 distance", "Negative inner product" }, SelectedIndex = 0 };
    readonly TextBox vectorInput = new() { PlaceholderText = "Vector array or selected map point", Text = "[0.1,0.2,0.3]", MinWidth = 230 }, vectorDetails = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Text = "Choose a vector column to inspect it. Projections are computed on this computer from the vectors the database returns." };
    readonly Canvas map = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(16, 100, 140, 170)) };
    readonly TextBlock mapCaption = Text("Select a vector column to begin", 12);
    readonly ListView historyList = new() { DisplayMemberPath = "Sql", SelectionMode = ListViewSelectionMode.Single };
    readonly TextBox historySearch = new() { PlaceholderText = "Search query history", Width = 260 };
    readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Height = 2 };
    readonly Dictionary<string, FrameworkElement> pages = []; readonly Dictionary<Guid, string> passwords = [];
    List<Profile> profiles = []; List<Relation> relations = []; List<QueryLog> history = []; Db? db; CancellationTokenSource? operation; Relation? current; int page; List<string> keys = []; List<PointData> points = []; Projection? projection;
    readonly Button run, cancel, connect; bool dialogOpen, restoringWorkspace; WorkspaceState workspace = new(); readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromSeconds(2) };
    sealed record PointData(string Metadata, double[] Values);
    static TextBlock Text(string text, double size = 14)
    {
        var ink=new SolidColorBrush();var label=new TextBlock{Text=text,FontSize=size<=13?size+1:size,TextWrapping=TextWrapping.Wrap,Foreground=ink};
        void Theme(){ink.Color=new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast?new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText):label.ActualTheme==ElementTheme.Dark?Windows.UI.Color.FromArgb(255,248,244,236):Windows.UI.Color.FromArgb(255,25,39,46);}
        label.Loaded+=(_,_)=>Theme();label.ActualThemeChanged+=(_,_)=>Theme();Theme();return label;
    }
    static Button Button(string text, RoutedEventHandler handler, bool accent = false) { var b = new Button { Content = text, Padding = new Thickness(13, 7, 13, 7), VerticalAlignment = VerticalAlignment.Center }; b.Click += handler; if (accent) { b.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; BrandAccent(b); } return b; }
    static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var i in items) p.Children.Add(i); return p; }
    static StackPanel Stack(params UIElement[] items) { var p = new StackPanel { Spacing = 10 }; foreach (var i in items) p.Children.Add(i); return p; }
    static ScrollViewer ScrollTools(FrameworkElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public MainWindow()
    {
        Title = "DB Cowboy"; SystemBackdrop = new MicaBackdrop(); Content = root;
        // Each file loads on its own: one that cannot be read is kept as it is and never overwritten this session.
        var unreadable = new List<UnreadableFile>();
        profiles = Store.Load<List<Profile>>(Store.ConnectionsFile, unreadable) ?? [];
        history = Store.Load<List<QueryLog>>(Store.HistoryFile, unreadable) ?? [];
        workspace = Store.Load<WorkspaceState>(WorkspaceState.File, unreadable) ?? new();
        // Only a defined theme is accepted; anything else in a hand-edited profile (such as "3") means Auto.
        root.RequestedTheme = Enum.TryParse<ElementTheme>(workspace.Preferences.Theme, out var theme) && Enum.IsDefined(theme) ? theme : ElementTheme.Default;
        Fluent.ReducedMotion = () => workspace.Preferences.ReducedMotion;
        RestorePlacement(); TrackPlacement(); Fluent.FollowContrast(root, ApplyAccentTheme);
        ApplyAccentTheme();root.ActualThemeChanged+=(_,_)=>ApplyAccentTheme();root.Loaded+=(_,_)=>ApplyAccentTheme();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        BrandControl(nav);
        var brand = BrandHeader();
        brand.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(BuildTitleBar(brand));
        Grid.SetRow(nav, 1); root.Children.Add(nav);
        var footer = BuildStatusStrip();
        connect = Button("Connect", ConnectClick, true);
        // The sidebar connection block, footer commands and page surface are built in MainWindow.Shell.cs.
        connectionExplorer = BuildConnectionBlock(); nav.PaneHeader = connectionExplorer; ApplyShellSurface();

        BuildNavigation();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(queryTabs, "SQL tabs");
        // Route after NavigationView finishes its own selection transaction.
        nav.ItemInvoked += (_, e) => { if (e.InvokedItemContainer is NavigationViewItem { Tag: string route }) DispatcherQueue.TryEnqueue(() => { if (!ShellCommand(route)) Navigate(route); }); };
        run = Button("Run statement", RunClick, true); cancel = Button("Cancel", (_, _) => operation?.Cancel()); cancel.IsEnabled = false;
        // The SQL command bar and editor view are built in MainWindow.QueryWorkspace.cs.
        pages["Query"] = BuildSqlEditorView();
        queryTabs.TabItemsChanged += (_,_) => SaveWorkspaceSoon();
        queryTabs.AddTabButtonClick += (_, _) => AddQuery(); queryTabs.TabCloseRequested += async (_, e) => await CloseQuery(e.Tab); queryTabs.SelectionChanged += (_, _) => SaveWorkspaceSoon();
        applyChangesButton = Button("Review & apply", ApplyChangesClick, true); discardChangesButton = Button("Discard", DiscardChangesClick); applyChangesButton.IsEnabled = discardChangesButton.IsEnabled = false;
        readOnly.Checked += (_, _) => tableReadOnly.IsChecked = true; readOnly.Unchecked += (_, _) => tableReadOnly.IsChecked = false;
        tableReadOnly.Checked += (_, _) => readOnly.IsChecked = true; tableReadOnly.Unchecked += (_, _) => readOnly.IsChecked = false;
        // The Tables page, including its paging and staging bar, is built in MainWindow.TableWorkspace.cs.
        pages["Table"] = BuildTableWorkspace();
        relationships.OpenRelation += async relation => await OpenRelation(relation); tableGrid.FollowReference += FollowForeignKey;
        pages["Vector lab"] = BuildVectorLab();
        // The Activity page is built in MainWindow.Activity.cs.
        pages["Activity"] = BuildActivityPage();
        pages["Home"] = BrandHome();
        pages["Connections"]=BuildConnectionLibrary();
        pages["Extensions"]=BuildExtensions();
        pages[ExtensionViewPage] = BuildExtensionViewPage();
        pages["Query"] = BuildQueryWorkspace(pages["Query"]);
        var content = new Grid { Padding = new Thickness(24, 16, 24, 12), RowSpacing = 6 }; content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); Grid.SetRow(progress, 1); content.Children.Add(progress);
        // Cards cast their hover shadow onto this empty layer behind the pages (a ThemeShadow receiver may not contain its caster).
        var shadowLayer = new Grid(); Grid.SetRow(shadowLayer, 2); content.Children.Add(shadowLayer); Fluent.AddShadowReceiver(shadowLayer);
        foreach (var view in pages.Values) { view.Visibility = Visibility.Collapsed; Grid.SetRow(view, 2); content.Children.Add(view); } 
        var workspaceShell = new Grid();
        foreach (var height in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) workspaceShell.RowDefinitions.Add(new() { Height = height });
        workspaceShell.Children.Add(content);
        Grid.SetRow(storageNotice, 1); workspaceShell.Children.Add(storageNotice);
        Grid.SetRow(feedback, 2); workspaceShell.Children.Add(feedback);
        Grid.SetRow(footer, 3); workspaceShell.Children.Add(footer);
        ShowUnreadableFiles(unreadable);
        nav.Content=workspaceShell; nav.SelectedItem = nav.MenuItems[0];ShowPage("Home");
        filter.TextChanged += (_, _) => RenderObjects(); tree.ItemInvoked += ObjectOpen;
        tableGrid.Inspect += (row, col) => Inspect(row[col]); root.KeyboardAccelerators.Add(Shortcut(VirtualKey.Enter, VirtualKeyModifiers.Control, () => RunClick(this, new()))); root.KeyboardAccelerators.Add(Shortcut(VirtualKey.Enter, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => RunAllClick(this, new()))); root.KeyboardAccelerators.Add(Shortcut(VirtualKey.K, VirtualKeyModifiers.Control, FocusTitleSearch)); root.KeyboardAccelerators.Add(Shortcut(VirtualKey.T, VirtualKeyModifiers.Control, () => { Navigate("Query"); AddQuery(); })); root.KeyboardAccelerators.Add(Shortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => operation?.Cancel()));
        autosave.Tick += (_, _) => { autosave.Stop(); SaveWorkspace(); }; LoadWorkspace(null); RefreshProfiles(); FilterHistory();
        if (Environment.GetCommandLineArgs().Contains("--integration-ui")) { var fixture = new Profile { Id = Guid.Parse("243816d0-aea0-4f06-bce8-e57a33873d53"), Name = "Cowboy lab / Docker", Host = "127.0.0.1", Port = 55439, Database = "cowboy_test", Username = "postgres", Ssl = "Disable", ReadOnly = true }; profiles = [fixture]; passwords[fixture.Id] = ""; RefreshProfiles(fixture); root.Loaded += (_, _) => ConnectClick(this, new()); }
        // Closing: let a running operation cancel first, and never drop staged row changes silently.
        AppWindow.Closing += async (_, e) =>
        {
            if (operation != null) { operation.Cancel(); e.Cancel = true; status.Text = "Cancelling operation. Close again after it completes."; return; }
            if (pendingChanges.Count > 0) { e.Cancel = true; await Notice("Pending changes", "Apply or discard staged row changes before closing."); return; }
            SaveWorkspace();
            if (db != null) await db.DisposeAsync();
        };
    }
    KeyboardAccelerator Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action) { var a = new KeyboardAccelerator { Key = key, Modifiers = modifiers }; a.Invoked += (_, e) => { e.Handled = true; action(); }; return a; }
    void ShowPage(string name)
    {
        currentRoute=name;
        if(connectionExplorer!=null)connectionExplorer.Visibility=Visibility.Visible;
        if(name == "Extensions" && db?.SessionIdentity != extensionSession) _ = reloadExtensions?.Invoke();
        if(name == "Home") refreshHome?.Invoke();
        if(name is "Query" or "Table" or "Vector lab" or "Activity")lastWorkspacePage=name;
        bool changed=pages[name].Visibility!=Visibility.Visible;
        string from=previousRoute??name; previousRoute=name;
        foreach(var view in pages)view.Value.Visibility=view.Key==name?Visibility.Visible:Visibility.Collapsed;
        // A Home card hands off to its page (connected animation); otherwise the page enters the way navigation moved.
        if(changed&&!Fluent.Connect("workspace-card",pages[name]))Fluent.PageIn(pages[name],MotionFor(from,name));
    }
    /// <summary>
    /// Opens a page by its route key. Routes that depend on an installed extension ("Vector lab", "ext:…")
    /// only open while that extension is in the connected database.
    /// </summary>
    void Navigate(string name)
    {
        if (name == "Structure") { selectTableView?.Invoke(1); name = "Table"; }
        pendingRoute = null;
        if (!RouteAvailable(name))
        {
            // Right after connecting the extension list is still on its way: remember the request and
            // open SQL meanwhile. RebuildDatabaseNavigation honours it if the page turns out to exist.
            bool listPending = db != null && !ExtensionListIsCurrent && extensionFailedSession != db.SessionIdentity;
            if (!listPending) return;
            pendingRoute = name; name = "Query";
        }
        var item = nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x => Equals(x.Tag, name));
        if (item == null) return;
        if (name.StartsWith(ExtensionRoutePrefix)) { showExtensionPage?.Invoke(name[ExtensionRoutePrefix.Length..]); ShowPage(ExtensionViewPage); }
        else ShowPage(name);
        nav.SelectedItem = item;
    }
    void FilterHistory() => historyList.ItemsSource = history.Where(q => q.Sql.Contains(historySearch.Text, StringComparison.OrdinalIgnoreCase) || q.Connection.Contains(historySearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
}













