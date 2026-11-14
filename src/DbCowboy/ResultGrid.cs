using System.Data;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
namespace DbCowboy;

public sealed class ResultGrid : Grid
{
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended };
    readonly StackPanel headers = new() { Orientation = Orientation.Horizontal };
    readonly TextBox search = new() { PlaceholderText = "Find in loaded rows…", Width = 230 };
    readonly TextBlock summary = BrandUi.MutedText("", 12);
    readonly TextBlock hints = BrandUi.MutedText("∅ is SQL NULL  ·  click a header to sort, right-click it to filter  ·  Ctrl+C copies selected rows", 12);
    // The empty state: optional Rowdy art, a heading, and one line saying what to do next.
    readonly Image emptyArt = new() { Width = 116, Height = 116, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    readonly TextBlock emptyTitle = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily("ms-appx:///Assets/Manrope.ttf#Manrope"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly TextBlock empty = BrandUi.MutedText("Run a query to see your data here.", 14);
    readonly StackPanel emptyPanel = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), MaxWidth = 460 };
    readonly FrameworkElement toolsRow, footerRow;
    string initialMessage = "Run a query to see your data here.";
    string initialTitle = "";
    string? initialArt;
    public string InitialMessage { get => initialMessage; set { initialMessage = value; if (Table == null) ShowEmpty(true, value); } }
    /// <summary>Heading shown above <see cref="InitialMessage"/> before any data has been loaded.</summary>
    public string InitialTitle { get => initialTitle; set { initialTitle = value; if (Table == null) ShowEmpty(true, initialMessage); } }
    /// <summary>Name of a mascot still in Assets/mascot (without "-light.png") shown before any data has been loaded.</summary>
    public string? InitialArt { get => initialArt; set { initialArt = value; if (Table == null) ShowEmpty(true, initialMessage); } }
    public string NoRowsMessage { get; set; } = "No rows returned. Your query completed successfully.";
    readonly Dictionary<int, double> widths = [];
    readonly Dictionary<int, string> columnFilters = [];
    int sortColumn = -1; bool descending;
    public DataTable? Table { get; private set; }
    public DataRow? Selected => list.SelectedItem is RowModel r ? r.Row : null;
    public IReadOnlyList<DataRow> SelectedRows => list.SelectedItems.OfType<RowModel>().Select(x => x.Row).ToList();
    public event Action<DataRow, int>? Inspect;
    public event Action<DataRow, int>? FollowReference;
    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    public DataTable VisibleTable { get { var table = Table?.Clone() ?? new(); foreach (var model in list.Items.OfType<RowModel>()) table.ImportRow(model.Row); return table; } }
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class RowModel(DataRow row)
    {
        public DataRow Row { get; } = row; public string[] Values { get; } = row.ItemArray.Select(v => v == DBNull.Value ? "∅" : Convert.ToString(v) ?? "").ToArray();
        /// <summary>The list item's accessible name: the first columns as "name: value", not the type name.</summary>
        public override string ToString() => string.Join(", ", Values.Take(6).Select((v, i) => $"{Row.Table.Columns[i].ColumnName}: {(v == "∅" ? "NULL" : v.Length > 80 ? v[..80] + "…" : v)}")) + (Values.Length > 6 ? $", and {Values.Length - 6} more columns" : "");
    }
    public ResultGrid()
    {
        RowSpacing = 0;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.MinHeightProperty, 28d), new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 8, 10, 8) };
        var clear = new Button { Content = "Clear filters", Padding = new Thickness(10, 6, 10, 6) }; ToolTipService.SetToolTip(clear, "Remove the search text and every column filter"); clear.Click += (_, _) => { columnFilters.Clear(); search.Text = ""; Apply(); };
        var copy = new Button { Content = "Copy selection", Padding = new Thickness(10, 6, 10, 6) }; ToolTipService.SetToolTip(copy, "Copy the selected rows with their column names (Ctrl+C)"); copy.Click += (_, _) => CopySelection();
        tools.Children.Add(search); tools.Children.Add(clear); tools.Children.Add(copy);
        toolsRow = new ScrollViewer { Content=tools,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
        Children.Add(toolsRow);
        AutomationProperties.SetName(search, "Search loaded result rows");
        AutomationProperties.SetName(list, "Result rows");
        var head = new ScrollViewer { Content = headers, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; SetRow(head, 1); Children.Add(head);
        var body = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; SetRow(body, 2); Children.Add(body);
        empty.TextAlignment = TextAlignment.Center; empty.HorizontalAlignment = HorizontalAlignment.Center;
        emptyPanel.Children.Add(emptyArt); emptyPanel.Children.Add(emptyTitle); emptyPanel.Children.Add(empty);
        AutomationProperties.SetAccessibilityView(emptyArt, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SetRow(emptyPanel, 2); Children.Add(emptyPanel);
        // A short results pane keeps the message and lets the art step aside instead of clipping it to a pair of boots.
        SizeChanged += (_, _) => FitEmptyArt();
        body.ViewChanged += (_, _) => head.ChangeView(body.HorizontalOffset, null, null, true); 
        // Footer: the row count on the left, the standing hints on the right, under a hairline.
        var footer = new Grid { ColumnSpacing = 16, Padding = new Thickness(12, 7, 12, 8) };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        summary.TextWrapping = hints.TextWrapping = TextWrapping.NoWrap; hints.TextTrimming = TextTrimming.CharacterEllipsis; hints.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(summary); SetColumn(hints, 1); footer.Children.Add(hints);
        footerRow = BrandUi.Themed(new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) }, (rule, dark) => rule.BorderBrush = BrandUi.HighContrast ? null : BrandUi.LineBrush(dark));
        SetRow(footerRow, 3); Children.Add(footerRow);
        ShowEmpty(true, initialMessage);
        search.TextChanged += (_, _) => Apply();
        list.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        list.DoubleTapped += (_, e) => { if (CellAt(e.OriginalSource as DependencyObject) is { } cell) Inspect?.Invoke(cell.Row, cell.Column); };
        list.RightTapped += (_, e) =>
        {
            if (CellAt(e.OriginalSource as DependencyObject) is not { } cell) return;
            var menu = Fluent.Acrylic(new MenuFlyout());
            void Item(string label, Action action) { var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
            Item("Inspect cell", () => Inspect?.Invoke(cell.Row, cell.Column));
            Item("Copy cell", () => ClipboardText(cell.Row[cell.Column] == DBNull.Value ? "NULL" : (string)cell.Row[cell.Column]));
            Item("Filter to this value", () => { columnFilters[cell.Column] = cell.Row[cell.Column] == DBNull.Value ? "@NULL" : "=" + (string)cell.Row[cell.Column]; Apply(); });
            Item("Follow foreign key", () => FollowReference?.Invoke(cell.Row, cell.Column));
            menu.ShowAt(list, e.GetPosition(list)); e.Handled = true;
        };
        var copyKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control }; copyKey.Invoked += (_, e) => { CopySelection(); e.Handled = true; }; list.KeyboardAccelerators.Add(copyKey);
    }
    (DataRow Row, int Column)? CellAt(DependencyObject? element)
    {
        int? column = null;
        while (element != null)
        {
            if (element is TextBlock { Tag: string tag } && int.TryParse(tag, out int index)) column = index;
            if (element is ListViewItem { Content: RowModel row } && column.HasValue) return (row.Row, column.Value);
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
    public void Show(DataTable data)
    {
        bool sameColumns = Table != null && Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).SequenceEqual(data.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        if (!sameColumns) { widths.Clear(); columnFilters.Clear(); sortColumn = -1; search.Text = ""; }
        Table = data; BuildColumns(); Apply();
    }
    public void FocusSearch() => search.Focus(FocusState.Keyboard);
    public void ShowSortMenu(FrameworkElement anchor)
    {
        if(Table==null)return;
        var menu=Fluent.Acrylic(new MenuFlyout());
        var reset=new MenuFlyoutItem {Text="Original loaded order"};
        reset.Click+=(_,_)=>{sortColumn=-1;BuildColumns();Apply();};menu.Items.Add(reset);
        menu.Items.Add(new MenuFlyoutSeparator());
        for(int i=0;i<Table.Columns.Count;i++)
        {
            int column=i;
            var options=new MenuFlyoutSubItem {Text=Table.Columns[i].ColumnName};
            foreach(bool reverse in new[]{false,true})
            {
                var item=new ToggleMenuFlyoutItem {Text=reverse?"Descending":"Ascending",IsChecked=sortColumn==column&&descending==reverse};
                item.Click+=(_,_)=>{sortColumn=column;descending=reverse;BuildColumns();Apply();};options.Items.Add(item);
            }
            menu.Items.Add(options);
        }
        menu.ShowAt(anchor);
    }
    void BuildColumns()
    {
        if (Table == null) return; headers.Children.Clear();
        for (int i = 0; i < Table.Columns.Count; i++)
        {
            int col = i; double width = widths.GetValueOrDefault(i, Math.Clamp(Table.Columns[i].ColumnName.Length * 8 + 70, 140, 260)); widths[i] = width;
            var header = BrandUi.Themed(new Grid { Width = width }, (cell, dark) => cell.Background = BrandUi.HighContrast ? null : BrandUi.HeaderBrush(dark));
            var button = new Button { Content = Table.Columns[i].ColumnName + (sortColumn == i ? descending ? " ↓" : " ↑" : ""), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(12, 7, 12, 7), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, CornerRadius = new CornerRadius(0) };
            ToolTipService.SetToolTip(button, "Click to sort · right-click (or Shift+F10) to filter · drag the right edge to resize");
            // Spoken without the arrow glyph; ContextRequested below also opens the filter from the keyboard.
            AutomationProperties.SetName(button, Table.Columns[i].ColumnName + (sortColumn == i ? descending ? ", sorted descending" : ", sorted ascending" : "") + (columnFilters.ContainsKey(i) ? ", filtered" : ""));
            button.Click += (_, _) => { descending = sortColumn == col && !descending; sortColumn = col; BuildColumns(); Apply(); };
            button.ContextRequested += (_, e) => { var input = new TextBox { Header = "Contains (or =exact / @NULL)", Text = columnFilters.GetValueOrDefault(col, ""), MinWidth = 240 }; var apply = new Button { Content = "Apply column filter", Margin = new Thickness(0, 10, 0, 0) }; var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(apply); var flyout = Fluent.Acrylic(new Flyout { Content = panel }); apply.Click += (_, _) => { if (input.Text.Length == 0) columnFilters.Remove(col); else columnFilters[col] = input.Text; Apply(); flyout.Hide(); }; flyout.ShowAt(button); e.Handled = true; };
            header.Children.Add(button); var grip = new Thumb { Width = 7, HorizontalAlignment = HorizontalAlignment.Right, Opacity = .2 }; AutomationProperties.SetName(grip, "Resize column " + Table!.Columns[col].ColumnName); ToolTipService.SetToolTip(grip, "Drag to resize"); grip.DragDelta += (_, e) => { widths[col] = Math.Clamp(widths[col] + e.HorizontalChange, 70, 800); header.Width = widths[col]; }; grip.DragCompleted += (_, _) => BuildColumns(); header.Children.Add(grip); headers.Children.Add(header);
        }
        string cells = string.Join("", Enumerable.Range(0, Table.Columns.Count).Select(i => $"<TextBlock Tag=\"{i}\" Text=\"{{Binding Values[{i}]}}\" Width=\"{widths[i].ToString(CultureInfo.InvariantCulture)}\" Padding=\"12,5\" FontFamily=\"Cascadia Mono, Consolas\" FontSize=\"12\" TextTrimming=\"CharacterEllipsis\" MaxLines=\"1\"/>"));
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\">" + cells + "</StackPanel></DataTemplate>"); list.MinWidth = widths.Values.Sum();
    }
    void Apply()
    {
        if (Table == null) return; IEnumerable<DataRow> rows = Table.Rows.Cast<DataRow>(); string find = search.Text.Trim();
        if (find.Length > 0) rows = rows.Where(r => r.ItemArray.Any(v => v != DBNull.Value && Convert.ToString(v)!.Contains(find, StringComparison.OrdinalIgnoreCase)));
        foreach (var (col, filter) in columnFilters) rows = rows.Where(r => filter == "@NULL" ? r[col] == DBNull.Value : r[col] != DBNull.Value && (filter.StartsWith('=') ? (string)r[col] == filter[1..] : ((string)r[col]).Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (sortColumn >= 0) { var comparer = Comparer<object>.Create(Compare); rows = descending ? rows.OrderByDescending(r => r[sortColumn], comparer) : rows.OrderBy(r => r[sortColumn], comparer); }
        var materialized = rows.Select(r => new RowModel(r)).ToList(); list.ItemsSource = materialized;
        ShowEmpty(materialized.Count == 0, Table.Rows.Count == 0 ? NoRowsMessage : "No loaded rows match these filters.");
        summary.Text = $"{materialized.Count:N0} of {Table.Rows.Count:N0} loaded rows  ·  {Table.Columns.Count} columns"; ViewChanged?.Invoke();
    }
    public static int Compare(object left, object right)
    {
        if (left == DBNull.Value) return right == DBNull.Value ? 0 : -1; if (right == DBNull.Value) return 1; string a = Convert.ToString(left)!, b = Convert.ToString(right)!;
        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return x.CompareTo(y); return StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }
    static void ClipboardText(string text) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
    void CopySelection() { if (Table == null || SelectedRows.Count == 0) return; ClipboardText(string.Join('\t', Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName)) + "\r\n" + string.Join("\r\n", SelectedRows.Select(r => string.Join('\t', r.ItemArray.Select(v => v == DBNull.Value ? "NULL" : Convert.ToString(v)))))); }
    public void Clear() { Table = null; headers.Children.Clear(); list.ItemsSource = null; summary.Text = ""; ShowEmpty(true, initialMessage); }

    bool artWanted;
    void FitEmptyArt() => emptyArt.Visibility = artWanted && ActualHeight is 0 or > 250 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows or hides the empty state. Art and heading belong only to the "nothing loaded yet" state.</summary>
    void ShowEmpty(bool show, string message)
    {
        bool initial = Table == null;
        empty.Text = message;
        emptyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        emptyTitle.Text = initialTitle; emptyTitle.Visibility = initial && initialTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        artWanted = initial && initialArt != null; FitEmptyArt();
        if (initial && initialArt != null && emptyArt.Source == null)
            emptyArt.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "mascot", initialArt + "-light.png")));
        // With nothing loaded there is nothing to search, copy or count, so the toolbar and footer stay out of the way.
        if (toolsRow != null && footerRow != null) toolsRow.Visibility = footerRow.Visibility = initial ? Visibility.Collapsed : Visibility.Visible;
    }
}


