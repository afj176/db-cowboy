using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

/// <summary>
/// A diagram of tables joined by their declared foreign keys. With a focus table it shows that table and
/// its direct neighbours; without one it shows the first tables of the schema.
/// </summary>
public sealed class RelationshipView : Grid
{
    const double CardWidth = 320, CardHeight = 164, ColumnStep = 410, RowStep = 235;

    readonly Canvas canvas = new();
    readonly TextBlock caption = BrandUi.MutedText("", 12);
    // The last arguments, so the diagram can be redrawn when the theme changes.
    IReadOnlyList<Relation> shownRelations = [];
    IReadOnlyList<ForeignKeyInfo> shownKeys = [];
    Relation? shownFocus;

    public event Action<Relation>? OpenRelation;

    public RelationshipView()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        caption.Margin = new Thickness(16, 12, 16, 4);
        Children.Add(caption);
        var viewer = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ZoomMode = ZoomMode.Enabled, MinZoomFactor = .25f, MaxZoomFactor = 2f };
        SetRow(viewer, 1); Children.Add(viewer);
        ActualThemeChanged += (_, _) => Show(shownRelations, shownKeys, shownFocus);
    }

    public void Show(IReadOnlyList<Relation> relations, IReadOnlyList<ForeignKeyInfo> keys, Relation? focus)
    {
        shownRelations = relations; shownKeys = keys; shownFocus = focus;
        canvas.Children.Clear();
        bool dark = ActualTheme == ElementTheme.Dark, contrast = BrandUi.HighContrast;
        static string Id(string schema, string name) => schema + "." + name;

        var chosenKeys = focus == null ? keys : keys.Where(k => k.SourceSchema == focus.Schema && k.SourceTable == focus.Name || k.TargetSchema == focus.Schema && k.TargetTable == focus.Name).ToList();
        var ids = chosenKeys.SelectMany(k => new[] { Id(k.SourceSchema, k.SourceTable), Id(k.TargetSchema, k.TargetTable) }).ToHashSet();
        if (focus != null) ids.Add(Id(focus.Schema, focus.Name));
        var nodes = relations.Where(r => ids.Contains(Id(r.Schema, r.Name))).Take(45).ToList();
        if (nodes.Count == 0) nodes = relations.Take(24).ToList();

        // Three cards per row.
        var positions = nodes.Select((r, i) => (Id: Id(r.Schema, r.Name), X: 40d + i % 3 * ColumnStep, Y: 40d + i / 3 * RowStep)).ToDictionary(x => x.Id);
        canvas.Width = 1260; canvas.Height = Math.Max(380, Math.Ceiling(nodes.Count / 3d) * RowStep + 60);

        var link = contrast ? null : BrandUi.AccentBrush(dark);
        foreach (var key in chosenKeys)
        {
            if (!positions.TryGetValue(Id(key.SourceSchema, key.SourceTable), out var a) || !positions.TryGetValue(Id(key.TargetSchema, key.TargetTable), out var b)) continue;
            bool left = b.X < a.X;
            double startX = left ? a.X : a.X + CardWidth, endX = left ? b.X + CardWidth : b.X;
            var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(startX, a.Y + 90) };
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Windows.Foundation.Point(startX + (left ? -60 : 60), a.Y + 90),
                Point2 = new Windows.Foundation.Point(endX + (left ? 60 : -60), b.Y + 90),
                Point3 = new Windows.Foundation.Point(endX, b.Y + 90)
            });
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            var path = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = 2, Data = geometry };
            var arrow = new TextBlock { Text = left ? "‹" : "›", FontSize = 28 };
            if (link != null) { path.Stroke = link; arrow.Foreground = link; }
            else path.Stroke = arrow.Foreground;
            ToolTipService.SetToolTip(path, $"{key.Name}\n{string.Join(", ", key.SourceColumns)} → {string.Join(", ", key.TargetColumns)}");
            canvas.Children.Add(path);
            Canvas.SetLeft(arrow, endX - (left ? 2 : 12)); Canvas.SetTop(arrow, b.Y + 69);
            canvas.Children.Add(arrow);
        }

        foreach (var relation in nodes)
        {
            var position = positions[Id(relation.Schema, relation.Name)];
            bool isFocus = focus != null && relation.Schema == focus.Schema && relation.Name == focus.Name;
            // Outgoing key columns first, then the columns other tables point at.
            var columnNames = chosenKeys.Where(k => k.SourceSchema == relation.Schema && k.SourceTable == relation.Name).SelectMany(k => k.SourceColumns.Select(c => "↗  " + c))
                .Concat(chosenKeys.Where(k => k.TargetSchema == relation.Schema && k.TargetTable == relation.Name).SelectMany(k => k.TargetColumns.Select(c => "◆  " + c))).Distinct().Take(5).ToList();

            var schema = BrandUi.MutedText(relation.Schema, 11);
            var name = new TextBlock { Text = relation.Name, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily("ms-appx:///Assets/Manrope.ttf#Manrope"), TextTrimming = TextTrimming.CharacterEllipsis };
            var columns = new TextBlock { Text = columnNames.Count == 0 ? "No declared foreign keys" : string.Join("\n", columnNames), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, MaxLines = 5 };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(schema); panel.Children.Add(name); panel.Children.Add(columns);

            var card = BrandUi.Card(panel, 18, 12);
            card.Width = CardWidth; card.MinHeight = CardHeight;
            if (isFocus && !contrast) { card.BorderThickness = new Thickness(2); card.Loaded += (_, _) => card.BorderBrush = BrandUi.AccentBrush(card.ActualTheme == ElementTheme.Dark); }
            card.DoubleTapped += (_, _) => OpenRelation?.Invoke(relation);
            ToolTipService.SetToolTip(card, "Double-click to browse " + relation.Schema + "." + relation.Name);
            Canvas.SetLeft(card, position.X); Canvas.SetTop(card, position.Y);
            canvas.Children.Add(card);
        }
        caption.Text = $"{nodes.Count} {(nodes.Count == 1 ? "table" : "tables")} · {chosenKeys.Count} declared foreign {(chosenKeys.Count == 1 ? "key" : "keys")} · arrows point to the referenced table · double-click a card to browse · Ctrl+wheel zooms";
    }
}
