using Microsoft.UI.Xaml.Automation;
using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DbCowboy;

public sealed class QueryTab
{
    public Guid Id { get; }
    public string Name { get; set; }
    public bool Favorite { get; set; }
    public TabViewItem Item { get; }
    public SqlEditor Editor { get; } = new();
    public TabView Outputs { get; } = InterfaceTabs.Apply(new TabView { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.SizeToContent },TabPresentation.Result);
    public TextBlock Summary { get; } = new() { Text = "Ready · run a statement or selection", FontSize = 12, Opacity = .7, Margin = new Thickness(4, 2, 4, 2) };
    readonly ResultGrid initial = new();
    readonly List<ChartDefinition> charts;
    readonly List<ResultGrid> resultGrids = [];
    bool truncated;
    public IntPtr WindowHandle { get; set; }
    public event Action? Changed;
    /// <summary>Shows the name, with a star for favorites; screen readers hear "favorite" instead of the glyph.</summary>
    public void RefreshHeader() { Item.Header = (Favorite ? "★ " : "") + Name; AutomationProperties.SetName(Item, Favorite ? Name + ", favorite" : Name); AutomationProperties.SetName(Outputs, "Results of " + Name); }
    public ResultGrid Results => Outputs.SelectedItem is TabViewItem { Content: ResultGrid grid } ? grid :
        Outputs.SelectedItem is TabViewItem { Content: ChartView chart } && chart.Definition.ResultSet >= 0 && chart.Definition.ResultSet < resultGrids.Count ? resultGrids[chart.Definition.ResultSet] :
        Outputs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<ResultGrid>().FirstOrDefault() ?? initial;
    public event Action<DataRow, int>? Inspect;
    public QueryTab(QueryDocument doc)
    {
        Id = doc.Id; Name = doc.Title; Favorite = doc.Favorite; Editor.Text = doc.Sql;
        charts = doc.Charts?.ToList() ?? [];
        Item = new TabViewItem { IsClosable = true }; RefreshHeader();
        // Editor card on top, a drag handle, then the results card. Row 0 is the editor; its height is what the handle changes.
        var split = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        split.RowDefinitions.Add(new() { Height = new GridLength(190), MinHeight = 112 });
        split.RowDefinitions.Add(new() { Height = new GridLength(12) });
        split.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 180 });

        Editor.Frameless();
        var editorCard = BrandUi.Card(Editor, 0, 10);
        // The card border doubles as the focus cue the borderless text box no longer draws.
        void EditorBorder(bool focused) { if (!BrandUi.HighContrast) editorCard.BorderBrush = focused ? BrandUi.AccentBrush(editorCard.ActualTheme == ElementTheme.Dark) : BrandUi.LineBrush(editorCard.ActualTheme == ElementTheme.Dark); }
        Editor.Input.GotFocus += (_, _) => EditorBorder(true);
        Editor.Input.LostFocus += (_, _) => EditorBorder(false);
        split.Children.Add(editorCard);

        var resize = new Thumb { HorizontalAlignment = HorizontalAlignment.Stretch, Opacity = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(resize, "Resize editor");
        ToolTipService.SetToolTip(resize, "Drag to resize the editor");
        var grip = BrandUi.Themed(new Border { Width = 44, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false },
            (bar, dark) => bar.Background = BrandUi.HighContrast ? null : BrandUi.LineBrush(dark));
        Grid.SetRow(grip, 1); split.Children.Add(grip);
        Grid.SetRow(resize, 1); split.Children.Add(resize);
        double EditorMaximum() => Math.Max(112, split.ActualHeight - 220);
        resize.DragDelta += (_, e) => split.RowDefinitions[0].Height = new GridLength(Math.Clamp(split.RowDefinitions[0].ActualHeight + e.VerticalChange, 112, EditorMaximum()));
        split.SizeChanged += (_, _) => { if (split.RowDefinitions[0].Height.Value > EditorMaximum()) split.RowDefinitions[0].Height = new GridLength(EditorMaximum()); };

        var output = new Grid();
        output.RowDefinitions.Add(new() { Height = GridLength.Auto });
        output.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Summary.Margin = new Thickness(14, 10, 14, 0);
        BrandUi.Themed(Summary, (label, dark) => { label.Opacity = 1; if (!BrandUi.HighContrast) label.Foreground = BrandUi.MutedBrush(dark); });
        output.Children.Add(Summary);
        // TabView aligns to the top by default, which left the card half empty under short results.
        Outputs.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(Outputs, 1); output.Children.Add(Outputs);
        var resultsCard = BrandUi.Card(output, 0, 10);
        Grid.SetRow(resultsCard, 2); split.Children.Add(resultsCard);

        initial.InitialArt = "01-got-you"; initial.InitialTitle = "Ready when you are";
        initial.InitialMessage = "Ctrl+Enter runs the statement at the caret, or your selection.";
        initial.Inspect += (row, col) => Inspect?.Invoke(row, col);
        Outputs.TabItems.Add(new TabViewItem { Header = "Results", Content = initial, IsClosable = false });
        Item.Content = split;
        Outputs.TabCloseRequested += (_, e) => { if (e.Tab.IsClosable) { if (e.Tab.Content is ChartView chart) { charts.RemoveAll(c => c.Id == chart.Definition.Id); Changed?.Invoke(); } Outputs.TabItems.Remove(e.Tab); } };
        foreach (var chart in charts) AddChart(chart);
    }
    public QueryDocument Document => new(Id, Name, Editor.Text, Favorite, charts.ToList());
    public void Show(QueryResult result, long milliseconds)
    {
        var selectedChart = (Outputs.SelectedItem as TabViewItem)?.Content as ChartView;
        truncated = result.Truncated; Outputs.TabItems.Clear(); resultGrids.Clear(); int index = 0;
        foreach (var table in result.Results)
        {
            var grid = new ResultGrid(); grid.Inspect += (row, col) => Inspect?.Invoke(row, col); grid.Show(table);
            resultGrids.Add(grid);
            grid.ViewChanged += () => { foreach (var view in Outputs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<ChartView>().Where(c => c.Definition.ResultSet == resultGrids.IndexOf(grid))) BindChart(view); };
            Outputs.TabItems.Add(new TabViewItem { Header = $"Result {++index} · {table.Rows.Count:N0}", Content = grid, IsClosable = false });
        }
        if (Outputs.TabItems.Count == 0) { initial.Show(result.Table); Outputs.TabItems.Add(new TabViewItem { Header = "Command", Content = initial, IsClosable = false }); }
        Outputs.SelectedIndex = 0;
        foreach (var chart in charts.ToList()) AddChart(chart);
        if (selectedChart != null) Outputs.SelectedItem = Outputs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Content is ChartView c && c.Definition.Id == selectedChart.Definition.Id) ?? Outputs.TabItems[0];
        Summary.Text = $"{result.Results.Count} {(result.Results.Count == 1 ? "result set" : "result sets")} · {milliseconds:N0} ms · {result.Affected:N0} {(result.Affected == 1 ? "affected row" : "affected rows")}" + (result.Truncated ? " · 2,000-row display cap per set" : "");
    }
    public void ShowChart()
    {
        if (Results.Table == null) return;
        if (charts.Count >= 12) { Summary.Text = "Keep up to 12 saved charts per query."; return; }
        int result = Math.Max(0, resultGrids.IndexOf(Results));
        var definition = ChartModel.Suggest(Results.VisibleTable, result); charts.Add(definition);
        Outputs.SelectedItem = AddChart(definition); Changed?.Invoke();
    }
    TabViewItem AddChart(ChartDefinition definition)
    {
        var chart = new ChartView(definition) { WindowHandle = WindowHandle };
        chart.Loaded += (_, _) => chart.WindowHandle = WindowHandle;
        var item = new TabViewItem { Header = definition.Title, Content = chart, IsClosable = true };
        chart.Changed += () => { int index = charts.FindIndex(c => c.Id == definition.Id); if (index >= 0) charts[index] = chart.Definition; item.Header = chart.Definition.Title; Changed?.Invoke(); };
        chart.ShowRows += () => { if (chart.Definition.ResultSet >= 0 && chart.Definition.ResultSet < resultGrids.Count) Outputs.SelectedItem = Outputs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Content, resultGrids[chart.Definition.ResultSet])); };
        BindChart(chart); Outputs.TabItems.Add(item); return item;
    }
    void BindChart(ChartView chart)
    {
        var index = chart.Definition.ResultSet;
        var grid = index >= 0 && index < resultGrids.Count ? resultGrids[index] : null;
        chart.Bind(grid?.VisibleTable, truncated, grid?.Table?.Rows.Count ?? 0);
    }
    public void ShowPlan(string json)
    {
        var plan = new QueryPlanView(); plan.Show(json); var item = new TabViewItem { Header = "Query plan", Content = plan, IsClosable = true }; Outputs.TabItems.Add(item); Outputs.SelectedItem = item;
    }
}

