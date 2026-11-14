using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
namespace DbCowboy;

public sealed partial class MainWindow
{
    static readonly System.Text.RegularExpressions.Regex SchemaChange = new(@"\b(CREATE|DROP|ALTER|RENAME|IMPORT\s+FOREIGN\s+SCHEMA)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    string Sql() => string.IsNullOrWhiteSpace(editor.SelectedText) ? SqlLanguage.CurrentStatement(editor.Text, editor.Caret) : editor.SelectedText;
    async void RunClick(object sender, RoutedEventArgs e) => await Execute(Sql());
    async void RunAllClick(object sender, RoutedEventArgs e) => await Execute(editor.Text);
    async Task Execute(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) { status.Text = "Place the caret inside a statement, select SQL, or choose Run all."; return; }
        var tab = ActiveQuery;
        await Work(async ct =>
        {
            var active = Connected();
            if (readOnly.IsChecked != true && !await Confirm("Execute with writes enabled?", $"{active.Profile.Name}{ProductionNote(active)}\n\n{sql[..Math.Min(sql.Length, 1400)]}")) { status.Text = "Execution dismissed."; return; }
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await active.Query(sql, ct, readOnly.IsChecked == true); timer.Stop(); tab.Show(result, timer.ElapsedMilliseconds); Navigate("Query");
                history.Insert(0, new(DateTime.Now, active.Profile.Name, sql, result.Results.Sum(t => t.Rows.Count), timer.ElapsedMilliseconds)); history = history.Take(250).ToList(); SaveHistory(); FilterHistory(); SaveWorkspaceSoon(); status.Text = $"Query complete · {timer.ElapsedMilliseconds:N0} ms · transaction committed";
                // Schema changes made here show up in the object list, completions and counts straight away.
                if (readOnly.IsChecked != true && SchemaChange.IsMatch(sql)) { await ReloadSchema(ct); status.Text += $" · schema reloaded · {RelationCount()}"; }
            }
            catch (Npgsql.PostgresException ex) { if (ex.Position > 0 && sql == tab.Editor.Text) tab.Editor.MarkError(ex.Position); throw; }
        });
    }
    async void ExplainClick(object sender, RoutedEventArgs e)
    {
        string sql = Sql(); if (string.IsNullOrWhiteSpace(sql)) return; var tab = ActiveQuery;
        await Work(async ct => { var result = await Connected().Query("EXPLAIN (FORMAT JSON) " + sql, ct, true); if (result.Table.Rows.Count == 0) return; tab.ShowPlan((string)result.Table.Rows[0][0]); status.Text = "Query plan ready · estimates only · query was not executed"; });
    }
    /// <summary>Shows one cell's full value. JSON is pretty-printed; the value can be copied as shown.</summary>
    async void Inspect(object value)
    {
        bool isNull = value == DBNull.Value;
        string text = isNull ? "SQL NULL" : Convert.ToString(value)!;
        bool json = false;
        try { text = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(text), new JsonSerializerOptions { WriteIndented = true }); json = !isNull; } catch (JsonException) { }
        if (dialogOpen) return;
        var facts = Row(Pill(isNull ? "NULL" : json ? "JSON" : "Text", isNull ? Tone.Neutral : Tone.Good));
        if (!isNull) facts.Children.Add(Pill($"{text.Length:N0} characters"));
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 480, MinWidth = 520, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Cell value");
        dialogOpen = true;
        try
        {
            var answer = await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "Cell value", Content = Stack(facts, box), PrimaryButtonText = isNull ? "" : "Copy value", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close, RequestedTheme = root.RequestedTheme }).ShowAsync();
            if (answer == ContentDialogResult.Primary)
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                status.Text = "Cell value copied";
            }
        }
        finally { dialogOpen = false; }
    }
    void CopyRow(DataRow? row) { if (row == null) return; var json = JsonSerializer.Serialize(PendingChange.Snapshot(row), new JsonSerializerOptions { WriteIndented = true }); var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(json); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); status.Text = "Row copied as JSON."; }
    async void OpenSqlClick(object sender, RoutedEventArgs e)
    {
        try { var picker = new FileOpenPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); picker.FileTypeFilter.Add(".sql"); var file = await picker.PickSingleFileAsync(); if (file != null) { AddQuery(new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(file.Name), await File.ReadAllTextAsync(file.Path))); Navigate("Query"); } } catch (Exception ex) { await Notice("Open SQL", ex.Message); }
    }
    async void SaveSqlClick(object sender, RoutedEventArgs e)
    {
        try { var tab = ActiveQuery; var picker = new FileSavePicker { SuggestedFileName = tab.Name }; WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); picker.FileTypeChoices.Add("SQL", new[] { ".sql" }); var file = await picker.PickSaveFileAsync(); if (file != null) { await File.WriteAllTextAsync(file.Path, tab.Editor.Text.Replace("\r", "\r\n")); status.Text = "Saved " + file.Name; } } catch (Exception ex) { await Notice("Save SQL", ex.Message); }
    }
    async void Export(DataTable? table)
    {
        if (table == null || table.Columns.Count == 0) { status.Text = "There are no result columns to export."; return; }
        try { var picker = new FileSavePicker { SuggestedFileName = "results" }; WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); picker.FileTypeChoices.Add("CSV", new[] { ".csv" }); picker.FileTypeChoices.Add("JSON", new[] { ".json" }); var file = await picker.PickSaveFileAsync(); if (file == null) return; if (file.FileType == ".json") await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(table.Rows.Cast<DataRow>().Select(PendingChange.Snapshot), new JsonSerializerOptions { WriteIndented = true })); else Db.Csv(table, file.Path); status.Text = $"Exported {table.Rows.Count:N0} visible rows to {file.Name}"; } catch (Exception ex) { await Notice("Export", ex.Message); }
    }
    async void ActivityClick(object sender, RoutedEventArgs e) => await Work(async ct => { var r = await Connected().Query("SELECT pid,usename,datname,state,wait_event_type,wait_event,now()-query_start AS age,left(query,1000) AS query FROM pg_stat_activity WHERE datname=current_database() ORDER BY query_start DESC NULLS LAST", ct, true); activityGrid.Show(r.Table); status.Text = "Server activity refreshed · visibility follows your database role"; });
    void HistoryLoadClick(object sender, RoutedEventArgs e) { if (historyList.SelectedItem is QueryLog q) { AddQuery(new(Guid.NewGuid(), q.At.ToString("HH:mm") + " / " + q.Connection, q.Sql)); Navigate("Query"); } }
    void ClearHistoryClick(object sender, RoutedEventArgs e) { history = []; SaveHistory(); FilterHistory(); }
    /// <summary>Everything the title bar search can run: commands, pages, saved connections, tables and favorite queries.</summary>
    Dictionary<string, Action> PaletteCommands()
    {
        var commands = new Dictionary<string, Action> {
            ["Connect to Postgres"] = () => ConnectClick(this, new()), ["New connection"] = () => NewConnectionClick(this, new()), ["Connection library"] = () => ConnectionsClick(this, new()), ["Edit selected connection"] = async () => await EditConnection(connections.SelectedItem as Profile), ["Disconnect"] = () => DisconnectClick(this, new()),
            ["New SQL tab"] = () => { AddQuery(); Navigate("Query"); }, ["Reopen closed query"] = ReopenQuery, ["Run current statement"] = () => RunClick(this, new()), ["Run all statements"] = () => RunAllClick(this, new()), ["Explain query plan"] = () => ExplainClick(this, new()), ["Format SQL"] = () => editor.Format(), ["Query library"] = () => SnippetsClick(this, new()), ["Save snippet"] = () => SaveSnippetClick(this, new()), ["Chart results"] = () => ActiveQuery.ShowChart()
        };
        foreach (var item in nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>()) { if (item.Tag is not string route || route.StartsWith('@')) continue; commands["Open / " + item.Content] = () => Navigate(route); }
        foreach(var profile in ConnectionProfiles.Ordered(profiles)){var p=profile;string label=$"Connect / {p.Name} · {p.Username}@{p.Host}:{p.Port}/{p.Database}";if(commands.ContainsKey(label))label+=" · "+p.Id.ToString()[..8];commands[label]=async()=>await ConnectProfile(p);}
        foreach (var relation in relations) { var r = relation; commands["Browse / " + r.Schema + "." + r.Name] = async () => await OpenRelation(r); }
        foreach (var query in queries.Where(q => q.Favorite)) { var q = query; commands["Favorite / " + q.Name] = () => { queryTabs.SelectedItem = q.Item; Navigate("Query"); }; }
        return commands;
    }
}

