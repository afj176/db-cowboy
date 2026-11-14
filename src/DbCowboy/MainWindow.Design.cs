using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DbCowboy;

/// <summary>
/// Brand presentation shared by the database pages. Colors come from branding/kit-v2/brand-contract.json;
/// Windows high contrast always wins over them.
/// </summary>
public sealed partial class MainWindow
{
    enum Tone { Neutral, Accent, Good, Warn }

    static class Brand
    {
        public static readonly Color Paper = Hex(0xF8F4EC), Ink = Hex(0x19272E), Muted = Hex(0x58656A), Copper = Hex(0xC76335), Action = Hex(0xA64F22),
            Night = Hex(0x101B21), Raised = Hex(0x1A2A33), Silver = Hex(0xB3C3CB), Amber = Hex(0xF5B761), Line = Hex(0x7B8A90),
            Teal = Hex(0x287F80), Mint = Hex(0x84C8BD), Danger = Hex(0xB43D3C), Cream = Hex(0xFFF0D2);
        static Color Hex(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        public static Color Fade(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
        public static readonly FontFamily Display = new("ms-appx:///Assets/Manrope.ttf#Manrope");
    }

    static bool HighContrast => new AccessibilitySettings().HighContrast;

    /// <summary>
    /// Scopes the brand accent onto one control before its template is created. Application-level
    /// brushes are shadowed by WinUI's own merged resources when the app theme differs from the system's,
    /// which is what left selection indicators in the Windows accent colour on the light theme.
    /// </summary>
    static T BrandControl<T>(T control) where T : FrameworkElement
    {
        foreach (var (theme, dictionary) in BrandThemeDictionaries()) control.Resources.ThemeDictionaries[theme] = dictionary;
        return control;
    }

    /// <summary>
    /// The same scoped accent for a dialog. Dialogs open in their own popup root, outside the window's resources,
    /// so without this their default button falls back to the Windows accent colour.
    /// </summary>
    static ResourceDictionary DialogBrand()
    {
        var resources = new ResourceDictionary();
        foreach (var (theme, dictionary) in BrandThemeDictionaries()) resources.ThemeDictionaries[theme] = dictionary;
        return resources;
    }

    /// <summary>
    /// The one dialog look: brand accent, a Manrope title and softer corners. Every ContentDialog in the app
    /// is created through this so they cannot drift apart.
    /// </summary>
    static ContentDialog StyleDialog(ContentDialog dialog)
    {
        dialog.Resources = DialogBrand();
        // The stock dialog is capped at 548 px, which clipped the row editor and the connection form.
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        if (dialog.Title is string title) dialog.Title = Display(title, 20);
        dialog.CornerRadius = new CornerRadius(12);
        return dialog;
    }

    /// <summary>A single card of labelled facts that wraps to more rows when the page is narrow.</summary>
    static Border StatStrip(params (string Label, string Value, string Tip)[] facts)
    {
        var cells = facts.Select(fact =>
        {
            var value = Text(fact.Value, 15);
            value.FontSize = 15; value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; value.TextWrapping = TextWrapping.NoWrap; value.TextTrimming = TextTrimming.CharacterEllipsis;
            var cell = new StackPanel { Spacing = 1 };
            cell.Children.Add(Eyebrow(fact.Label)); cell.Children.Add(value);
            if (fact.Tip.Length > 0) ToolTipService.SetToolTip(cell, fact.Tip);
            AutomationProperties.SetName(cell, fact.Label + ": " + fact.Value);
            return (FrameworkElement)cell;
        }).ToArray();
        return Surface(Flow(130, cells), 14);
    }

    static IEnumerable<(string Theme, ResourceDictionary Dictionary)> BrandThemeDictionaries()
    {
        string[] fills = ["AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "NavigationViewSelectionIndicatorForeground", "ListViewItemSelectionIndicatorBrush", "ProgressBarForeground", "ProgressRingForegroundThemeBrush", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked", "TextControlBorderBrushFocused", "HyperlinkButtonForeground", "HyperlinkButtonForegroundPointerOver", "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed"];
        string[] onAccent = ["AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "TextOnAccentFillColorPrimaryBrush"];
        foreach (var (theme, color, text) in new[] { ("Light", Brand.Action, Microsoft.UI.Colors.White), ("Default", Brand.Amber, Brand.Night) })
        {
            var dictionary = new ResourceDictionary();
            foreach (var key in fills) dictionary[key] = new SolidColorBrush(color);
            foreach (var key in onAccent) dictionary[key] = new SolidColorBrush(text);
            // Checked toggle buttons (the theme switch) take the accent as fill with the matching text colour.
            foreach (var state in new[] { "", "PointerOver", "Pressed" })
            {
                dictionary["ToggleButtonBackgroundChecked" + state] = new SolidColorBrush(color);
                dictionary["ToggleButtonBorderBrushChecked" + state] = new SolidColorBrush(color);
                dictionary["ToggleButtonForegroundChecked" + state] = new SolidColorBrush(text);
            }
            yield return (theme, dictionary);
        }
        // Windows high contrast keeps its own colours.
        yield return ("HighContrast", new ResourceDictionary());
    }

    /// <summary>Runs <paramref name="apply"/> now and whenever the element's theme changes, passing whether it is dark.</summary>
    static T Themed<T>(T element, Action<T, bool> apply) where T : FrameworkElement
    {
        void Update() => apply(element, element.ActualTheme == ElementTheme.Dark);
        element.Loaded += (_, _) => Update(); element.ActualThemeChanged += (_, _) => Update(); Update();
        return element;
    }

    /// <summary>A Manrope heading. Body text and controls stay on the system font.</summary>
    static TextBlock Display(string text, double size = 28)
    {
        var label = Text(text, size);
        label.FontSize = size; label.FontFamily = Brand.Display; label.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        label.LineHeight = size * 1.15; label.CharacterSpacing = -10;
        return label;
    }

    static TextBlock Muted(string text, double size = 13)
    {
        var label = Text(text, size); label.FontSize = size;
        return Themed(label, (l, dark) => { if (!HighContrast) l.Foreground = new SolidColorBrush(dark ? Brand.Silver : Brand.Muted); });
    }

    /// <summary>A small uppercase section label.</summary>
    static TextBlock Eyebrow(string text)
    {
        var label = Muted(text.ToUpperInvariant(), 11);
        label.CharacterSpacing = 80; label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return label;
    }

    /// <summary>The Windows high contrast text colour, for strokes and marks that must stay visible in every contrast theme.</summary>
    static SolidColorBrush SystemText() => new(new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText));

    static Color ToneColor(Tone tone, bool dark) => tone switch
    {
        Tone.Accent => dark ? Brand.Amber : Brand.Action,
        Tone.Good => dark ? Brand.Mint : Brand.Teal,
        Tone.Warn => dark ? Color.FromArgb(255, 240, 140, 135) : Brand.Danger,
        _ => dark ? Brand.Silver : Brand.Muted
    };

    /// <summary>A compact status label: tinted fill with matching text so state reads at a glance.</summary>
    static Border Pill(string text, Tone tone = Tone.Neutral)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
        var pill = new Border { Child = label, Padding = new Thickness(9, 3, 9, 4), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1) };
        return Themed(pill, (p, dark) =>
        {
            if (HighContrast) { p.ClearValue(Border.BackgroundProperty); label.ClearValue(TextBlock.ForegroundProperty); p.BorderBrush = label.Foreground; return; }
            var color = ToneColor(tone, dark);
            p.Background = new SolidColorBrush(Brand.Fade(color, dark ? (byte)46 : (byte)30)); p.BorderBrush = new SolidColorBrush(Brand.Fade(color, 90)); label.Foreground = new SolidColorBrush(color);
        });
    }

    /// <summary>A brand surface: white on paper in light, raised on night in dark.</summary>
    static Border Surface(FrameworkElement child, double padding = 20, double radius = 12) => BrandUi.Card(child, padding, radius);

    /// <summary>One headline number with its label; <paramref name="set"/> updates it in place.</summary>
    static Border Tile(string label, string value, out Action<string, string?> set, string caption = "")
    {
        var number = Display(value, 26); var detail = Muted(caption, 12); detail.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var content = Stack(Eyebrow(label), number, detail); content.Spacing = 2;
        set = (next, nextCaption) => { number.Text = next; if (nextCaption != null) { detail.Text = nextCaption; detail.Visibility = nextCaption.Length > 0 ? Visibility.Visible : Visibility.Collapsed; } };
        var tile = Surface(content, 16); tile.MinWidth = 150;
        AutomationProperties.SetName(tile, label);
        return tile;
    }

    /// <summary>Lays children out in equal columns and reflows to fewer columns when the row gets narrow.</summary>
    static Grid Flow(double minColumn, params FrameworkElement[] children) => Flow(minColumn, true, children);

    /// <param name="stretch">When false, a short row keeps its column width instead of stretching a lone card across the page.</param>
    static Grid Flow(double minColumn, bool stretch, params FrameworkElement[] children)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (var child in children) grid.Children.Add(child);
        void Arrange()
        {
            int columns = Math.Clamp((int)((grid.ActualWidth + 12) / (minColumn + 12)), 1, stretch ? Math.Max(1, children.Length) : 12);
            if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count > 0) return;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < (children.Length + columns - 1) / columns; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i % columns); Grid.SetRow(children[i], i / columns); }
        }
        grid.SizeChanged += (_, _) => Arrange(); Arrange();
        return grid;
    }

    /// <summary>One row for <see cref="Bars"/>, with the element types spelled out so callers need no casts.</summary>
    static (string Label, double Value, string Display, string? Detail, Action? Open) Bar(string label, double value, string display, string? detail = null, Action? open = null) =>
        (label, value, display, detail, open);

    /// <summary>Horizontal bars that share one scale. Each row may carry a click action.</summary>
    static StackPanel Bars(IReadOnlyList<(string Label, double Value, string Display, string? Detail, Action? Open)> items, Tone tone = Tone.Accent)
    {
        var panel = new StackPanel { Spacing = 10 };
        double max = items.Count == 0 ? 0 : items.Max(i => i.Value);
        foreach (var item in items)
        {
            var label = Text(item.Label, 13); label.FontSize = 13; label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            var value = Text(item.Display, 13); value.FontSize = 13; value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; value.TextWrapping = TextWrapping.NoWrap;
            var heading = new Grid { ColumnSpacing = 12 };
            heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(label); Grid.SetColumn(value, 1); heading.Children.Add(value);
            var fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
            var track = new Grid { Height = 8 };
            // In high contrast the rail is an outline in the system text colour, so the bar's extent stays visible.
            var rail = Themed(new Border { CornerRadius = new CornerRadius(4) }, (r, dark) => { if (HighContrast) { r.Background = null; r.BorderThickness = new Thickness(1); r.BorderBrush = SystemText(); } else { r.BorderThickness = new Thickness(0); r.Background = new SolidColorBrush(Brand.Fade(dark ? Brand.Silver : Brand.Line, 36)); } });
            track.Children.Add(rail); track.Children.Add(fill);
            Themed(fill, (f, dark) => f.Background = HighContrast ? SystemAccent() : new SolidColorBrush(ToneColor(tone, dark)));
            double share = max <= 0 ? 0 : item.Value / max;
            // A non-zero value always keeps a visible sliver so it is not mistaken for nothing.
            track.SizeChanged += (_, _) => fill.Width = item.Value <= 0 ? 0 : Math.Max(4, track.ActualWidth * share);
            var row = new StackPanel { Spacing = 4 }; row.Children.Add(heading); row.Children.Add(track);
            if (!string.IsNullOrEmpty(item.Detail)) { var detail = Muted(item.Detail, 12); detail.MaxLines = 2; detail.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(detail); }
            ToolTipService.SetToolTip(row, item.Label + " · " + item.Display);
            if (item.Open == null) { panel.Children.Add(row); continue; }
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 10), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            button.Click += (_, _) => item.Open(); AutomationProperties.SetName(button, item.Label + ", " + item.Display);
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>A plain read-only table for short catalog listings.</summary>
    static FrameworkElement MiniTable(string[] headers, IEnumerable<string[]> rows, double[]? weights = null)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 0 };
        for (int c = 0; c < headers.Length; c++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(weights?[c] ?? 1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int c = 0; c < headers.Length; c++) { var head = Eyebrow(headers[c]); head.Margin = new Thickness(0, 0, 0, 6); Grid.SetColumn(head, c); grid.Children.Add(head); }
        int r = 1;
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var rule = Themed(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top }, (b, dark) => b.Background = HighContrast ? SystemText() : new SolidColorBrush(Brand.Fade(dark ? Brand.Silver : Brand.Line, 40)));
            Grid.SetRow(rule, r); Grid.SetColumnSpan(rule, headers.Length); grid.Children.Add(rule);
            for (int c = 0; c < headers.Length && c < row.Length; c++)
            {
                var cell = Text(row[c], 13); cell.FontSize = 13; cell.Margin = new Thickness(0, 8, 0, 8); cell.MaxLines = 3; cell.TextTrimming = TextTrimming.CharacterEllipsis; cell.IsTextSelectionEnabled = true;
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
            }
            r++;
        }
        return grid;
    }

    /// <summary>Rowdy's reaction art. The transparent light still is used on both themes, by the owner's choice.</summary>
    static Image Reaction(string name, double size = 120)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "reactions", name + "-light.png"))) };
        AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return image;
    }

    /// <summary>A centered empty or blocked state: one character moment, what is true, and the next step.</summary>
    static FrameworkElement EmptyState(string reaction, string title, string body, params UIElement[] actions)
    {
        var heading = Display(title, 22); heading.TextAlignment = TextAlignment.Center;
        var detail = Muted(body, 14); detail.TextAlignment = TextAlignment.Center; detail.MaxWidth = 460;
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 32, 24, 32) };
        panel.Children.Add(Reaction(reaction, 132)); panel.Children.Add(heading); panel.Children.Add(detail);
        if (actions.Length > 0) { var row = Row(actions); row.HorizontalAlignment = HorizontalAlignment.Center; row.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(row); }
        return panel;
    }

    /// <summary>
    /// The one page header: Manrope title, a one-line description, and the page's primary actions on the right.
    /// The actions drop under the title when the page gets narrow.
    /// </summary>
    static Grid PageHeader(TextBlock title, TextBlock description, FrameworkElement? actions = null)
    {
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        description.MaxLines = 2; description.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(title); text.Children.Add(description);
        var header = new Grid { ColumnSpacing = 16, RowSpacing = 10, Margin = new Thickness(0, 2, 0, 4) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header.Children.Add(text);
        if (actions == null) return header;
        actions.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(actions);
        void Arrange()
        {
            bool narrow = header.ActualWidth < 760;
            Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        header.SizeChanged += (_, _) => Arrange(); Arrange();
        return header;
    }

    static Grid PageHeader(string title, string description, params UIElement[] actions) =>
        PageHeader(Display(title, 30), Muted(description, 14), actions.Length == 0 ? null : ScrollTools(Row(actions)));

    /// <summary>A toolbar button with an icon and a label. The tooltip says what it does and names its shortcut.</summary>
    static Button ToolButton(Symbol icon, string label, RoutedEventHandler handler, string? tip = null, bool accent = false)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new Viewbox { Width = 15, Height = 15, Child = new SymbolIcon(icon), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(text);
        var button = new Button { Content = content, Padding = new Thickness(11, 7, 13, 7), VerticalAlignment = VerticalAlignment.Center };
        button.Click += handler;
        AutomationProperties.SetName(button, label);
        if (tip != null) ToolTipService.SetToolTip(button, tip);
        if (accent) { button.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; BrandAccent(button); }
        return button;
    }

    /// <summary>An icon-only button. It always carries an accessible name and a tooltip.</summary>
    static Button IconButton(IconElement icon, string name, RoutedEventHandler handler)
    {
        var button = new Button { Content = new Viewbox { Width = 16, Height = 16, Child = icon }, Padding = new Thickness(9, 8, 9, 8), VerticalAlignment = VerticalAlignment.Center };
        button.Click += handler;
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    static Button IconButton(Symbol icon, string name, RoutedEventHandler handler) => IconButton(new SymbolIcon(icon), name, handler);

    /// <summary>A thin vertical rule that separates groups inside a command bar.</summary>
    static Border ToolSeparator() =>
        Themed(new Border { Width = 1, Height = 22, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center },
            (rule, dark) => rule.Background = HighContrast ? SystemAccent() : BrandUi.LineBrush(dark));

    /// <summary>
    /// A command bar: one group on the left, one on the right. When both no longer fit on one row,
    /// the right group drops underneath instead of being clipped.
    /// </summary>
    static Grid CommandRow(UIElement[] leading, UIElement[] trailing)
    {
        var bar = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        bar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        bar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        // Each group wraps onto further lines when even it alone is too wide, so a primary action such as
        // Run statement is never scrolled out of sight in a narrow window (reflow instead of a hidden scroll).
        WrapRow leftRow = new(leading), rightRow = new(trailing);
        FrameworkElement left = leftRow, right = rightRow;
        bar.Children.Add(left); bar.Children.Add(right);
        void Arrange()
        {
            var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            leftRow.Measure(unbounded); rightRow.Measure(unbounded);
            double available = bar.ActualWidth - bar.Padding.Left - bar.Padding.Right;
            bool wrap = available > 0 && available < leftRow.DesiredSize.Width + rightRow.DesiredSize.Width + bar.ColumnSpacing;
            Grid.SetRow(right, wrap ? 1 : 0); Grid.SetColumn(right, wrap ? 0 : 1); Grid.SetColumnSpan(right, wrap ? 2 : 1);
            Grid.SetColumnSpan(left, wrap ? 2 : 1);
        }
        // Buttons appear, disappear and change width after the bar has been sized, so the groups are watched too.
        bar.SizeChanged += (_, _) => Arrange();
        leftRow.SizeChanged += (_, _) => Arrange(); rightRow.SizeChanged += (_, _) => Arrange();
        Arrange();
        return bar;
    }

    /// <summary>Keeps reading-width content on the left: it fills the space up to <paramref name="maxWidth"/> and no further.</summary>
    static Grid LeftCapped(FrameworkElement content, double maxWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        // The slack beyond the cap goes to this empty column.
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(.0001, GridUnitType.Star) });
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>A scatter of points fitted to the canvas with the data's own aspect ratio preserved.</summary>
    static FrameworkElement PointMap(IReadOnlyList<(double X, double Y)> points, double height = 300)
    {
        var canvas = new Canvas { Height = height };
        var frame = Surface(canvas, 0, 8); frame.Padding = new Thickness(12);
        void Draw()
        {
            canvas.Children.Clear();
            double width = canvas.ActualWidth; if (width <= 0 || points.Count == 0) return;
            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            double spanX = Math.Max(maxX - minX, 1e-9), spanY = Math.Max(maxY - minY, 1e-9), scale = Math.Min(width / spanX, height / spanY);
            double offsetX = (width - spanX * scale) / 2, offsetY = (height - spanY * scale) / 2;
            bool dark = canvas.ActualTheme == ElementTheme.Dark;
            var fill = HighContrast ? SystemAccent() : new SolidColorBrush(Brand.Fade(ToneColor(Tone.Good, dark), 170));
            foreach (var point in points)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = fill };
                Canvas.SetLeft(dot, offsetX + (point.X - minX) * scale - 3);
                // Screen Y grows downward; northing grows upward.
                Canvas.SetTop(dot, height - offsetY - (point.Y - minY) * scale - 3);
                canvas.Children.Add(dot);
            }
        }
        canvas.SizeChanged += (_, _) => Draw(); canvas.ActualThemeChanged += (_, _) => Draw();
        return frame;
    }
}

/// <summary>Lays its children out left to right with 8 px gaps and starts a new line when the next one does not fit.</summary>
sealed class WrapRow : Panel
{
    const double Gap = 8;
    public WrapRow(IEnumerable<UIElement> items) { foreach (var item in items) Children.Add(item); }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, line = 0, width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue;   // collapsed
            if (x > 0 && x + size.Width > available.Width) { height += line + Gap; width = Math.Max(width, x - Gap); x = 0; line = 0; }
            x += size.Width + Gap; line = Math.Max(line, size.Height);
        }
        width = Math.Max(width, x - Gap); height += line;
        return new(Math.Max(0, width), height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var lines = new List<List<UIElement>>(); var current = new List<UIElement>(); double x = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) { child.Arrange(new(0, 0, 0, 0)); continue; }
            if (x > 0 && x + size.Width > final.Width) { lines.Add(current); current = []; x = 0; }
            current.Add(child); x += size.Width + Gap;
        }
        if (current.Count > 0) lines.Add(current);
        double y = 0;
        foreach (var items in lines)
        {
            double line = items.Max(c => c.DesiredSize.Height), left = 0;
            // Items in a line share its vertical centre, like a horizontal StackPanel with centred children.
            foreach (var child in items) { var size = child.DesiredSize; child.Arrange(new(left, y + (line - size.Height) / 2, size.Width, size.Height)); left += size.Width + Gap; }
            y += line + Gap;
        }
        return final;
    }
}