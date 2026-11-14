using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace DbCowboy;

public sealed partial class MainWindow
{
    Db Connected() => db ?? throw new InvalidOperationException("Connect to a Postgres database first.");
    /// <summary>The line every write confirmation carries for a connection marked as production.</summary>
    static string ProductionNote(Db active) => active.Profile.Production ? "\n\nThis connection is marked as PRODUCTION." : "";
    async Task Notice(string title, string message)
    {
        if (dialogOpen) { status.Text = message; return; } dialogOpen = true;
        try { await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 500 }, CloseButtonText = "Close", RequestedTheme = root.RequestedTheme }).ShowAsync(); } finally { dialogOpen = false; }
    }
    async Task<bool> Confirm(string title, string message, string action = "Execute")
    {
        if (dialogOpen) return false; dialogOpen = true;
        try { return await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 }, MaxHeight = 500 }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = root.RequestedTheme }).ShowAsync() == ContentDialogResult.Primary; } finally { dialogOpen = false; }
    }
    async Task Work(Func<CancellationToken, Task> action, bool vectorReadOnly = false)
    {
        if (operation != null) { status.Text = "An operation is running. Cancel it before starting another."; return; }
        vectorColumn.IsEnabled=vectorModel.IsEnabled=false;operation = new(); RefreshVectorExperimentActions(); RefreshConnectionChrome(); run.IsEnabled = connect.IsEnabled = false; cancel.IsEnabled = true; if(vectorCancel!=null)vectorCancel.IsEnabled=true; progress.Visibility = Visibility.Visible; feedback.IsOpen = false; status.Text = "Working…";
        try { await action(operation.Token); }
        catch (OperationCanceledException) { status.Text = connectingProfile!=null?"Connection cancelled · current workspace retained":vectorReadOnly ? "Cancelled · Vector Lab is read-only; no changes were issued" : "Cancelled · refresh affected data before retrying writes"; }
        catch (Exception e)
        {
            string message = e is Npgsql.PostgresException pg ? $"{pg.MessageText}\nSQLSTATE {pg.SqlState}" + (pg.Position > 0 ? $" · character {pg.Position}" : "") + (pg.Hint == null ? "" : "\n" + pg.Hint) : e.Message;
            status.Text = connectingProfile!=null?(db==null?"Connection failed":"Connection failed · "+db.Profile.Name+" is still active"):"Operation failed"; feedback.Title = connectingProfile!=null?"Could not connect to "+profiles.FirstOrDefault(p=>p.Id==connectingProfile)?.Name:"Could not complete operation"; feedback.Message = message; feedback.Severity = InfoBarSeverity.Error; feedback.IsOpen = true;
            var copy = new Button { Content = "Copy details" }; copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(message); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); }; feedback.ActionButton = copy;
        }
        finally { vectorColumn.IsEnabled=vectorModel.IsEnabled=true;operation.Dispose(); operation = null; RefreshVectorExperimentActions(); RefreshConnectionChrome(); run.IsEnabled = connect.IsEnabled = true; cancel.IsEnabled = false; if(vectorCancel!=null)vectorCancel.IsEnabled=false; progress.Visibility = Visibility.Collapsed; }
    }
    void RefreshProfiles(Profile? selected = null)
    {
        Guid? id=selected?.Id??(connections.SelectedItem as Profile)?.Id??workspace.LastSelectedConnection??db?.Profile.Id;
        refreshingProfiles=true;try{connections.ItemsSource=null;connections.ItemsSource=ConnectionProfiles.Ordered(profiles).ToList();connections.SelectedItem=profiles.FirstOrDefault(p=>p.Id==id)??ConnectionProfiles.Ordered(profiles).FirstOrDefault();}finally{refreshingProfiles=false;}
        workspace.LastSelectedConnection=(connections.SelectedItem as Profile)?.Id;RefreshConnectionChrome();SaveWorkspaceSoon();
    }
    async void NewConnectionClick(object sender, RoutedEventArgs e) => await EditConnection(null);
    void ConnectionsClick(object sender,RoutedEventArgs e){Navigate("Connections");RenderConnectionLibrary();}
    async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if(operation!=null||connectingProfile!=null||dialogOpen)return;
        if(connections.SelectedItem is not Profile profile){Navigate("Connections");await EditConnection(null);return;}
        await ConnectProfile(profile);
    }
    bool SavedSettingsAreActive(Profile profile)=>db?.Profile.Id==profile.Id&&passwords.TryGetValue(profile.Id,out var password)&&activeSessionKey==ConnectionProfiles.SessionKey(profile,password);
    async Task ConnectProfile(Profile requested,bool reconnect=false,string? knownPassword=null)
    {
        if(operation!=null||connectingProfile!=null||dialogOpen)return;
        if(pendingChanges.Count>0){await Notice("Pending changes","Apply or discard staged changes before switching or reconnecting.");return;}
        var p=profiles.FirstOrDefault(p=>p.Id==requested.Id)??requested;
        if(!reconnect&&SavedSettingsAreActive(p)){Navigate(lastWorkspacePage);status.Text="Using active session · "+db!.Profile.Name;return;}
        connections.SelectedItem=p;
        (string Password,bool? Remember)? credential=knownPassword!=null?(knownPassword,null):await ConnectionPassword(p);if(credential==null){status.Text=db==null?"Connection cancelled":"Connection cancelled · "+db.Profile.Name+" remains active";return;}
        string password=credential.Value.Password;connectingProfile=p.Id;RefreshConnectionChrome();
        try{await Work(async ct=>
        {
            PreparedConnection prepared;
            try{prepared=await PreparedConnection.Open(p,password,ct);}
            catch(OperationCanceledException){connectionErrors.Remove(p.Id);throw;}
            catch(Npgsql.PostgresException ex)when(ex.SqlState is "28P01" or "28000"){passwords.Remove(p.Id);requestPassword.Add(p.Id);connectionErrors[p.Id]="Authentication failed · try again to enter a new password";throw;}
            catch(SshHostKeyChangedException){connectionErrors[p.Id]="The SSH server's host key changed · edit the connection to review it";throw;}
            catch{connectionErrors[p.Id]=db==null?"Could not connect · check saved details":"Could not connect · "+db.Profile.Name+" remains active";throw;}
            SaveWorkspace();var previous=db;db=prepared.Database;activeSessionKey=ConnectionProfiles.SessionKey(p,password);passwords[p.Id]=password;requestPassword.Remove(p.Id);connectionErrors.Remove(p.Id);
            relations=prepared.Relations;completionCatalog=prepared.Completions;foreignKeys=prepared.ForeignKeys;current=null;keys=[];currentColumns=[];filter.Text="";
            tableGrid.Clear();columnsGrid.Clear();indexesGrid.Clear();foreignKeysGrid.Clear();ResetVectorExploration();vectorColumn.ItemsSource=prepared.Vectors;vectorColumn.SelectedIndex=prepared.Vectors.Count>0?0:-1;readOnly.IsChecked=p.ReadOnly;
            UpdateConnectionIdentity(p);
            LoadWorkspace(p.Id);await RefreshVectorSources(prepared.Vectors,ct,prepared.Sources);RenderObjects();relationships.Show(relations,foreignKeys,null);
            SaveProfile(ConnectionProfiles.WithCredentials(p,password,credential.Value.Remember) with{LastConnected=DateTimeOffset.Now,SshHostKey=prepared.Database.TunnelHostKey??p.SshHostKey});
            if(previous!=null)await previous.DisposeAsync();
            string restoreNote="";
            if(workspace.Connections.TryGetValue(p.Id,out var saved)&&saved.Schema!=null&&saved.Table!=null&&relations.FirstOrDefault(r=>r.Schema==saved.Schema&&r.Name==saved.Table)is{} restored)
            {current=restored;page=0;try{await Structure(ct);await Browse(ct);}catch(OperationCanceledException){restoreNote=" · previous table restore cancelled";}catch{current=null;tableGrid.Clear();restoreNote=" · previous table unavailable; choose another";}}
            tableOpened?.Invoke();Navigate(lastWorkspacePage);SaveWorkspaceSoon();status.Text=$"Connected · {p.Name} · {RelationCount()} · TLS {p.Ssl}"+(p.Ssh?" · over SSH":"")+restoreNote;
        },vectorReadOnly:true);}
        finally{connectingProfile=null;RefreshConnectionChrome();}
    }
    async void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (operation != null) { operation.Cancel(); return; } if(connectingProfile!=null)return;if (pendingChanges.Count > 0) { await Notice("Pending changes", "Apply or discard staged changes before disconnecting."); return; }
        SaveWorkspace(); if (db != null) { await db.DisposeAsync(); db = null; } activeSessionKey=null;relations = []; current = null; tableOpened?.Invoke(); foreignKeys = []; completionCatalog = []; RenderObjects(); LoadWorkspace(null); tableGrid.Clear(); columnsGrid.Clear(); indexesGrid.Clear(); foreignKeysGrid.Clear();ResetVectorExploration(); vectorColumn.ItemsSource = null; vectorSources.Clear();vectorCards.Children.Clear();AddVectorDisconnectedState();ShowVectorSurface("sources"); status.Text = "Disconnected · saved connections are ready to reopen"; UpdateConnectionIdentity(null);RefreshConnectionChrome();
    }
    void RenderObjects()
    {
        tree.RootNodes.Clear(); foreach (var group in relations.Where(x => (x.Schema + "." + x.Name).Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.Schema)) { var schema = new TreeViewNode { Content = group.Key, IsExpanded = true }; foreach (var relation in group) schema.Children.Add(new TreeViewNode { Content = relation }); tree.RootNodes.Add(schema); }
    }
    async void RefreshClick(object sender, RoutedEventArgs e) => await Work(async ct => { await ReloadSchema(ct); vectorColumn.ItemsSource = await Connected().Vectors(ct); vectorColumn.SelectedIndex = 0; await RefreshVectorSources((vectorColumn.ItemsSource as IEnumerable<VectorColumn>)?.ToList()??[],ct); status.Text = $"Schema refreshed · {RelationCount()}"; });
    string RelationCount() => $"{relations.Count:N0} {(relations.Count == 1 ? "relation" : "relations")}";
    /// <summary>
    /// Reads tables, completions and foreign keys again and redraws everything that lists them, so counts
    /// and the object tree follow DDL run here or objects created and dropped outside the app.
    /// </summary>
    async Task ReloadSchema(CancellationToken ct)
    {
        var active = Connected();
        relations = await active.Relations(ct); completionCatalog = await active.Completions(ct); foreignKeys = await active.ForeignKeys(ct);
        foreach (var query in queries) query.Editor.Catalog = completionCatalog;
        if (current != null && !relations.Any(r => r.Schema == current.Schema && r.Name == current.Name)) { current = null; tableGrid.Clear(); columnsGrid.Clear(); indexesGrid.Clear(); foreignKeysGrid.Clear(); tableOpened?.Invoke(); }
        RenderObjects(); relationships.Show(relations, foreignKeys, current);
    }
    async void ObjectOpen(TreeView sender, TreeViewItemInvokedEventArgs e) { if (e.InvokedItem is TreeViewNode { Content: Relation relation }) await OpenRelation(relation); }
    async Task OpenRelation(Relation relation)
    {
        if (operation != null) return; if (pendingChanges.Count > 0) { await Notice("Pending changes", "Apply or discard changes before browsing another table."); return; }
        bool gone = false;
        await Work(async ct =>
        {
            var old = current; current = relation; page = 0; keys = [];
            try { await Structure(ct); await Browse(ct); Navigate("Table"); tableOpened?.Invoke(); SaveWorkspaceSoon(); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01") { current = old; gone = true; }
            catch { current = old; throw; }
        });
        // The table was dropped since the list was read: reload the list rather than show a raw error.
        if (gone) await Work(async ct => { await ReloadSchema(ct); status.Text = $"{relation.Schema}.{relation.Name} no longer exists · list refreshed · {RelationCount()}"; });
    }
    async Task Structure(CancellationToken ct)
    {
        if (current == null) return; var active = Connected(); currentColumns = await active.Columns(current, ct);
        var columnTable = new DataTable(); foreach (string name in new[] { "Column", "Type", "Nullable", "Generated", "Default" }) columnTable.Columns.Add(name); foreach (var col in currentColumns) columnTable.Rows.Add(col.Name, col.Type, col.Nullable ? "yes" : "no", col.Generated ? "yes" : "no", (object?)col.Default ?? DBNull.Value); columnsGrid.Show(columnTable);
        var indexes = await active.IndexUsage(current, ct); indexesGrid.Show(indexes.Table);
        var pk = await active.Query("SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=ANY(i.indkey) WHERE i.indrelid=to_regclass(@r) AND i.indisprimary ORDER BY a.attnum", ct, true, ("r", current.Qualified)); keys = pk.Table.Rows.Cast<DataRow>().Select(x => (string)x[0]).ToList();
        var fkTable = new DataTable(); foreach (string name in new[] { "Constraint", "From", "Columns", "To", "Referenced columns" }) fkTable.Columns.Add(name); foreach (var fk in foreignKeys.Where(k => k.SourceSchema == current.Schema && k.SourceTable == current.Name || k.TargetSchema == current.Schema && k.TargetTable == current.Name)) fkTable.Rows.Add(fk.Name, fk.SourceSchema + "." + fk.SourceTable, string.Join(", ", fk.SourceColumns), fk.TargetSchema + "." + fk.TargetTable, string.Join(", ", fk.TargetColumns)); foreignKeysGrid.Show(fkTable); relationships.Show(relations, foreignKeys, current);
    }
    async Task Browse(CancellationToken ct)
    {
        if (current == null) throw new InvalidOperationException("Open a table from the explorer."); var order = keys.Count > 0 ? " ORDER BY " + string.Join(",", keys.Select(Db.Q)) : "";
        var result = await Connected().Query($"SELECT * FROM {current.Qualified}{order} LIMIT 250 OFFSET @offset", ct, true, ("offset", page * 250)); tableGrid.Show(result.Table);
        pageSummary.Text = $"Page {page + 1} · {result.Table.Rows.Count:N0} rows loaded · " + (keys.Count > 0 ? "primary-key order" : "no primary key, so page order can vary"); status.Text = "Table loaded · right-click a cell for inspection, filtering and foreign-key navigation";
    }
    async void PreviousClick(object sender, RoutedEventArgs e) { if (operation != null) return; if (pendingChanges.Count > 0) { await Notice("Pending changes", "Apply or discard staged changes before changing pages."); return; } page = Math.Max(0, page - 1); await Work(Browse); }
    async void NextClick(object sender, RoutedEventArgs e) { if (operation != null || tableGrid.Table?.Rows.Count < 250) return; if (pendingChanges.Count > 0) { await Notice("Pending changes", "Apply or discard staged changes before changing pages."); return; } page++; await Work(Browse); }
    async void ReloadClick(object sender, RoutedEventArgs e) { if (pendingChanges.Count > 0) { await Notice("Pending changes", "Apply or discard staged changes before reloading."); return; } await Work(Browse); }
    void FollowForeignKey(DataRow row, int column)
    {
        if (current == null) return; string name = row.Table.Columns[column].ColumnName; var fk = foreignKeys.FirstOrDefault(f => f.SourceSchema == current.Schema && f.SourceTable == current.Name && f.SourceColumns.Contains(name));
        if (fk == null) { status.Text = "This column has no declared outgoing foreign key."; return; }
        if (fk.SourceColumns.Any(c => row[c] == DBNull.Value)) { status.Text = "This row has a NULL foreign key."; return; }
        string Literal(string value) => "E'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
        var sql = $"SELECT * FROM {Db.Q(fk.TargetSchema)}.{Db.Q(fk.TargetTable)} WHERE " + string.Join(" AND ", fk.TargetColumns.Select((target, i) => $"{Db.Q(target)}::text = {Literal((string)row[fk.SourceColumns[i]])}")) + ";";
        AddQuery(new(Guid.NewGuid(), "Reference / " + fk.TargetTable, sql)); Navigate("Query"); status.Text = "Foreign-key lookup opened in a new tab. Run when ready.";
    }
}







