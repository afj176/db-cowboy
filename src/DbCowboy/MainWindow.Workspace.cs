using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DbCowboy;

public sealed partial class MainWindow
{
    void AddQuery(QueryDocument? document = null)
    {
        if (queries.Count >= 30) { status.Text = "Keep up to 30 query tabs open per connection."; return; }
        var tab = new QueryTab(document ?? new(Guid.NewGuid(), "Query " + (queries.Count + 1), "-- Ctrl+Space completes from your connected schema.\rSELECT current_database(), current_user;"));
        tab.Editor.Catalog = completionCatalog;
        tab.Editor.Changed += SaveWorkspaceSoon; tab.Changed += SaveWorkspaceSoon;
        tab.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this); tab.Inspect += (row, col) => Inspect(row[col]);
        var tabMenu=Fluent.Acrylic(new MenuFlyout());
        var moveLeft=new MenuFlyoutItem{Text="Move left",Icon=new SymbolIcon(Symbol.Back)};
        var moveRight=new MenuFlyoutItem{Text="Move right",Icon=new SymbolIcon(Symbol.Forward)};
        moveLeft.Click+=(_,_)=>MoveQuery(tab.Item,-1);moveRight.Click+=(_,_)=>MoveQuery(tab.Item,1);
        tabMenu.Items.Add(moveLeft);tabMenu.Items.Add(moveRight);
        tabMenu.Opening+=(_,_)=>{var index=queryTabs.TabItems.IndexOf(tab.Item);moveLeft.IsEnabled=operation==null&&index>0;moveRight.IsEnabled=operation==null&&index>=0&&index<queryTabs.TabItems.Count-1;};
        tab.Item.ContextFlyout=tabMenu;
        queries.Add(tab); queryTabs.TabItems.Add(tab.Item); queryTabs.SelectedItem = tab.Item; SaveWorkspaceSoon();
        if (!restoringWorkspace) DispatcherQueue.TryEnqueue(() => tab.Editor.Input.Focus(FocusState.Programmatic));
    }
    void MoveQuery(TabViewItem item,int direction)
    {
        if(operation!=null)return;
        int from=queryTabs.TabItems.IndexOf(item),to=from+direction;
        if(from<0||to<0||to>=queryTabs.TabItems.Count)return;
        queryTabs.TabItems.RemoveAt(from);queryTabs.TabItems.Insert(to,item);queryTabs.SelectedItem=item;SaveWorkspaceSoon();
    }
    Task CloseQuery(TabViewItem item)
    {
        if (operation != null) { status.Text = "Wait for the running operation before closing query tabs."; return Task.CompletedTask; }
        var tab = queries.FirstOrDefault(q => q.Item == item); if (tab == null) return Task.CompletedTask;
        workspace.ClosedDocuments.Insert(0, tab.Document); workspace.ClosedDocuments = workspace.ClosedDocuments.Take(20).ToList();
        queryTabs.TabItems.Remove(item); queries.Remove(tab); if (queries.Count == 0) AddQuery(); SaveWorkspaceSoon(); return Task.CompletedTask;
    }
    void SaveWorkspaceSoon() { if (restoringWorkspace || vectorRestoring) return; autosave.Stop(); autosave.Start(); }
    /// <summary>Writes workspace.json. Returns false only when the write failed; the reason is in the status line.</summary>
    bool SaveWorkspace()
    {
        if (restoringWorkspace || vectorRestoring || queries.Count == 0) return true;
        var docs = queryTabs.TabItems.OfType<TabViewItem>().Select(item => queries.First(q => q.Item == item).Document).ToList();
        RememberVectorInvestigation();
        var state = new ConnectionWorkspace(docs, ActiveQuery.Id, current?.Schema, current?.Name,lastWorkspacePage);
        if (db == null) workspace.Scratch = state; else workspace.Connections[db.Profile.Id] = state;
        // An unreadable workspace.json stays untouched; the notice shown at startup already says so.
        if (Store.IsUnreadable(WorkspaceState.File)) return true;
        try { workspace.Save(); return true; } catch (Exception e) { status.Text = "Autosave could not write workspace: " + e.Message; return false; }
    }

    /// <summary>Writes history.json, unless it could not be read at startup. A failed write never fails the query that was run.</summary>
    void SaveHistory()
    {
        if (Store.IsUnreadable(Store.HistoryFile)) return;
        try { Store.Write(Store.HistoryFile, history); } catch (Exception e) { status.Text = "Query history could not be saved: " + e.Message; }
    }

    /// <summary>Says which profile files could not be read, where their copies are, and what will not be saved this session.</summary>
    void ShowUnreadableFiles(IReadOnlyList<UnreadableFile> files)
    {
        if (files.Count == 0) return;
        string Consequence(string file) => file switch
        {
            Store.ConnectionsFile => "Connections you add or change work for this session but are not saved.",
            Store.HistoryFile => "Queries you run are not added to the saved history.",
            _ => "Query tabs, snippets and the theme you choose are not saved."
        };
        var lines = files.Select(f =>
            $"{f.File} could not be read ({f.Reason}). It has not been changed" +
            (f.Backup.Length > 0 ? $", and a copy is kept as {f.Backup}." : ".") +
            $" Until DB Cowboy restarts it will not be written: {Consequence(f.File)}");
        storageNotice.Title = files.Count == 1 ? "A saved file could not be read" : "Some saved files could not be read";
        storageNotice.Message = string.Join("\n", lines) + $"\nThe files are in {Store.Root}. Repair or remove them, then restart.";
        storageNotice.IsOpen = true;
        status.Text = storageNotice.Title + " · " + string.Join(", ", files.Select(f => f.File)) + " left unchanged";
    }
    void LoadWorkspace(Guid? id)
    {
        restoringWorkspace = true;
        try
        {
            var state = id.HasValue && workspace.Connections.TryGetValue(id.Value, out var connection) ? connection : id.HasValue ? new ConnectionWorkspace([], Guid.Empty) : workspace.Scratch;
            lastWorkspacePage=state.View is "Query" or "Table" or "Structure" or "Vector lab" or "Activity"?state.View:"Query";
            queries.Clear(); queryTabs.TabItems.Clear(); foreach (var doc in state.Documents.Take(30)) AddQuery(doc);
            if (queries.Count == 0) AddQuery(new(Guid.NewGuid(), "Getting started", "-- Ctrl+Enter runs · Ctrl+Space completes\rSELECT current_database(), current_user, version();"));
            queryTabs.SelectedItem = queries.FirstOrDefault(q => q.Id == state.ActiveDocument)?.Item ?? queries[0].Item;
        }
        finally { restoringWorkspace = false; }
    }
    async void RenameTabClick(object sender, RoutedEventArgs e)
    {
        if (dialogOpen) return; var tab = ActiveQuery; var name = new TextBox { Header = "Query name", Text = tab.Name, MaxLength = 60 }; var favorite = new CheckBox { Content = "Favorite this query", IsChecked = tab.Favorite };
        dialogOpen = true; ContentDialogResult result;
        try { result = await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "Query details", Content = Stack(name, favorite), PrimaryButtonText = "Save", CloseButtonText = "Cancel", RequestedTheme = root.RequestedTheme }).ShowAsync(); } finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        tab.Name = name.Text.Trim(); tab.Favorite = favorite.IsChecked == true; tab.RefreshHeader(); SaveWorkspaceSoon();
    }
    async void SaveSnippetClick(object sender, RoutedEventArgs e)
    {
        if (dialogOpen) return; var sql = string.IsNullOrWhiteSpace(editor.SelectedText) ? editor.Text : editor.SelectedText; var name = new TextBox { Header = "Snippet name", Text = ActiveQuery.Name, MaxLength = 80 };
        dialogOpen = true; ContentDialogResult result;
        try { result = await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "Save to query library", Content = name, PrimaryButtonText = "Save snippet", CloseButtonText = "Cancel", RequestedTheme = root.RequestedTheme }).ShowAsync(); } finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return; workspace.Snippets.Add(new(Guid.NewGuid(), name.Text.Trim(), sql)); SaveWorkspace(); status.Text = "Snippet saved locally.";
    }
    /// <summary>The query library: saved snippets with a preview. Opening one never runs it.</summary>
    async void SnippetsClick(object sender, RoutedEventArgs e)
    {
        if (dialogOpen) return;
        var list = BrandControl(new ListView { ItemsSource = workspace.Snippets.ToList(), MaxHeight = 380, MinWidth = 520, SelectionMode = ListViewSelectionMode.Single });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Saved snippets");
        // Rows are built in code: the name, then the first part of the SQL.
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid/></DataTemplate>");
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is not Grid cell || args.Item is not SavedSnippet snippet) return;
            cell.Children.Clear();
            if (args.InRecycleQueue) return;
            var name = Text(snippet.Name, 14); name.FontSize = 14; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var preview = Muted(string.Join(" ", snippet.Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 12);
            preview.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"); preview.TextWrapping = TextWrapping.NoWrap; preview.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new StackPanel { Spacing = 2, Margin = new Thickness(0, 7, 0, 8) };
            row.Children.Add(name); row.Children.Add(preview);
            cell.Children.Add(row);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, snippet.Name);
        };
        bool empty = workspace.Snippets.Count == 0;
        if (!empty) list.SelectedIndex = 0;
        FrameworkElement body = empty
            ? EmptyState("01-got-you", "No snippets yet", "Save a query from SQL with More, then Save as snippet. Snippets stay on this computer.")
            : Stack(Muted("Open a saved query in a new tab. Nothing runs until you run it.", 13), list);
        dialogOpen = true; ContentDialogResult result;
        try { result = await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "Query library", Content = body, PrimaryButtonText = empty ? "" : "Open in new tab", SecondaryButtonText = empty ? "" : "Delete selected", CloseButtonText = empty ? "Close" : "Cancel", DefaultButton = empty ? ContentDialogButton.Close : ContentDialogButton.Primary, RequestedTheme = root.RequestedTheme }).ShowAsync(); } finally { dialogOpen = false; }
        if (list.SelectedItem is not SavedSnippet chosen) return;
        if (result == ContentDialogResult.Primary) { Navigate("Query"); AddQuery(new(Guid.NewGuid(), chosen.Name, chosen.Sql)); }
        if (result == ContentDialogResult.Secondary) { workspace.Snippets.Remove(chosen); SaveWorkspace(); status.Text = "Snippet deleted · " + chosen.Name; }
    }
    void ReopenQuery()
    {
        if (workspace.ClosedDocuments.Count == 0) return; var doc = workspace.ClosedDocuments[0]; workspace.ClosedDocuments.RemoveAt(0); AddQuery(doc); Navigate("Query");
    }
}

