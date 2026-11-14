using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
namespace DbCowboy;

public sealed partial class MainWindow
{
    readonly ComboBox vectorMode = new() { ItemsSource = new[] { "Constellation", "PCA atlas", "Similarity matrix" }, SelectedIndex = 0, MinWidth = 150 };
    readonly ComboBox vectorModel = new() { PlaceholderText = "Vector set", MinWidth = 180, MaxWidth = 320 };
    readonly TextBox vectorSearch = new() { PlaceholderText = "Find a label in this table…", MinWidth = 190 };
    readonly TextBlock vectorFocus = Text("Choose an embedding to begin", 20), vectorComparison = Text("Pin a reference, then select another row to compare.", 12);
    readonly TextBlock vectorMeaning = new() { FontSize=12,TextWrapping=TextWrapping.Wrap,MaxLines=3,TextTrimming=TextTrimming.CharacterEllipsis };
    readonly Canvas fingerprint = new() { Height = 70, MinWidth = 220 };
    readonly StackPanel vectorNeighbors = new() { Spacing = 6 };
    readonly TextBlock vectorEvidence = Text("Read-only · projections are computed on this computer", 11);
    VectorSource? vectorSource; bool populatingScope; int focusedVector, vectorGeneration; List<VectorItem> vectorItems = []; VectorItem? comparisonReference;
    readonly Dictionary<PointData,Dictionary<string,string>> pointMetadata = [];
    Dictionary<string,int> vectorLabelCounts=[];
    Button? vectorCancel, vectorBack;
    readonly Stack<int> vectorTrail = [];

    /// <summary>
    /// Vector lab: pick a stored vector column, load a sample, then read it three ways (neighbour constellation,
    /// PCA atlas, similarity matrix) beside a lens on the focused row. Every query is read-only.
    /// </summary>
    FrameworkElement BuildVectorLab()
    {
        AutomationProperties.SetName(vectorSearch, "Search labels in this table");
        AutomationProperties.SetName(vectorMode, "Vector visualization mode");
        AutomationProperties.SetName(vectorModel, "Vector set recorded in this column");
        AutomationProperties.SetName(vectorColumn, "Vector column");
        ToolTipService.SetToolTip(vectorColumn, "The table and vector column to explore");
        ToolTipService.SetToolTip(vectorMode, "How the loaded sample is drawn");
        foreach (var control in new FrameworkElement[] { vectorColumn, vectorModel, vectorMode, colorBy, vectorSearch }) BrandControl(control);
        vectorColumn.MinWidth = 220; vectorColumn.MaxWidth = 300; vectorModel.MaxWidth = 250;
        mapViewport.Content = map;

        // ---- Source bar: what to load, and the other two surfaces ----
        var lensToggle = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = "Focus lens", Visibility = Visibility.Collapsed };
        ToolTipService.SetToolTip(lensToggle, "Swap the map for the focused row's details");
        var explore = ToolButton(Symbol.Play, "Explore", MapClick, "Load a stable sample of up to 500 rows from this column", accent: true);
        vectorCancel = Button("Cancel", (_, _) => operation?.Cancel());
        vectorCancel.IsEnabled = false;
        ToolTipService.SetToolTip(vectorCancel, "Cancel the running read (Esc)");
        // Cancel only exists while a read is running.
        void ShowCancel() => vectorCancel.Visibility = vectorCancel.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        vectorCancel.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => ShowCancel());
        ShowCancel();
        var backToMap = ToolButton(Symbol.Back, "Back to map", (_, _) => ShowVectorSurface("explore"), "Return to the loaded sample");
        var health = ToolButton(Symbol.Repair, "Health", VectorHealthClick, "Coverage, missing vectors and zero vectors for this column");
        vectorSourcesButton.Padding = vectorSavedButton.Padding = new Thickness(13, 7, 13, 7);
        ToolTipService.SetToolTip(vectorSourcesButton, "List every vector column in this database");
        ToolTipService.SetToolTip(vectorSavedButton, "Save this investigation, reopen a saved one, or reset the zoom");
        var sourceBar = CommandRow([vectorColumn, vectorModel, explore, vectorCancel], [lensToggle, backToMap, vectorSourcesButton, health, vectorSavedButton]);
        var sourceNote = Muted("", 13);

        // ---- Map tools: how the sample is drawn and searched ----
        var find = ToolButton(Symbol.Find, "Find", MapClick, "Load the rows whose label matches this text (Enter)");
        AutomationProperties.SetName(find, "Find words");
        vectorLabelsButton.Padding = new Thickness(13, 7, 13, 7); vectorLabelsButton.MaxWidth = 260;
        ToolTipService.SetToolTip(vectorLabelsButton, "Choose which column names each point");
        vectorCompareButton.Click += ComparePipelinesClick; vectorQualityButton.Click += SearchQualityClick;
        vectorCompareButton.HorizontalAlignment = vectorQualityButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        var morePanel = new StackPanel { Spacing = 8, Width = 260 };
        morePanel.Children.Add(Eyebrow("Checks on the focused row"));
        morePanel.Children.Add(vectorCompareButton); morePanel.Children.Add(vectorQualityButton);
        morePanel.Children.Add(Muted("Both need a loaded sample with a row in focus.", 12));
        var more = new DropDownButton { Content = "More", Padding = new Thickness(12, 7, 10, 7), Flyout = Fluent.Acrylic(new Flyout { Content = morePanel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight }) };
        AutomationProperties.SetName(more, "More vector commands");
        colorBy.Visibility = Visibility.Collapsed;
        var mapBar = CommandRow([vectorMode, colorBy, vectorSearch, find], [vectorLabelsButton, more]);
        mapBar.Padding = new Thickness(12, 10, 12, 8);

        // ---- Stage: the map with its caption lines ----
        vectorHealth.TextWrapping = TextWrapping.NoWrap; vectorHealth.TextTrimming = TextTrimming.CharacterEllipsis;
        mapCaption.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        // The legend and the method note are reference text; they sit back from the caption and the counts.
        foreach (var note in new[] { vectorLegend, vectorEvidence })
            Themed(note, (label, dark) => { if (!HighContrast) label.Foreground = BrandUi.MutedBrush(dark); });
        var captions = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 14, 8) };
        captions.Children.Add(mapCaption); captions.Children.Add(vectorHealth); captions.Children.Add(vectorLegend); captions.Children.Add(vectorEvidence);
        var stageLayout = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) stageLayout.RowDefinitions.Add(new() { Height = height });
        stageLayout.Children.Add(mapBar);
        Grid.SetRow(captions, 1); stageLayout.Children.Add(captions);
        Grid.SetRow(mapViewport, 2); stageLayout.Children.Add(mapViewport);
        var stage = Surface(stageLayout, 0);

        // ---- Focus lens: the selected row, its fingerprint, a pinned comparison and its neighbours ----
        vectorBack = IconButton(Symbol.Back, "Back to the previous focus", (_, _) => { if (vectorTrail.TryPop(out int index)) FocusVector(index, false); });
        vectorBack.IsEnabled = false;
        vectorFocus.FontFamily = Brand.Display; vectorFocus.FontWeight = Microsoft.UI.Text.FontWeights.Bold; vectorFocus.FontSize = 20;
        var lensTitle = new Grid();
        var lensLabel = Eyebrow("Focus");
        lensLabel.VerticalAlignment = VerticalAlignment.Center;
        vectorBack.HorizontalAlignment = HorizontalAlignment.Right;
        lensTitle.Children.Add(lensLabel); lensTitle.Children.Add(vectorBack);
        var pin = Button("Pin as reference", (_, _) => PinVectorReference());
        ToolTipService.SetToolTip(pin, "Keep this row as the reference, then focus another row to compare the two");
        var clearPin = Button("Clear pin", (_, _) => { comparisonReference = null; RefreshComparison(); SaveWorkspaceSoon(); });
        var neighbors = ToolButton(Symbol.Find, "Nearest in database", NeighborsClick, "Ask PostgreSQL for the exact 25 nearest rows to this vector", accent: true);
        AutomationProperties.SetName(neighbors, "Search database neighbors");
        neighbors.HorizontalAlignment = HorizontalAlignment.Stretch;
        var advanced = new Expander { Header = "Paste a vector", Content = Stack(metric, vectorInput), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        metric.HorizontalAlignment = vectorInput.HorizontalAlignment = HorizontalAlignment.Stretch; vectorInput.MinWidth = 0;
        vectorDetails.MinHeight = 180;
        var side = Stack(lensTitle, vectorFocus, vectorMeaning,
            Eyebrow("Dimension fingerprint"), fingerprint,
            ScrollTools(Row(pin, clearPin)), vectorComparison,
            neighbors, advanced,
            Eyebrow("Nearby in loaded rows · cosine"), vectorNeighbors,
            Eyebrow("Row context"), vectorDetails);
        ToolTipService.SetToolTip(fingerprint, "Signed mean of each band of dimensions. Amber is positive, teal is negative.");
        var sidebar = Surface(new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(14, 12, 14, 12) }, 0);

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(310) });
        grid.Children.Add(stage);
        Grid.SetColumn(sidebar, 1); grid.Children.Add(sidebar);
        // A narrow window shows the map or the lens, not both squeezed into strips.
        void Reflow()
        {
            bool compact = grid.ActualWidth < 700;
            bool lens = compact && lensToggle.IsChecked == true;
            lensToggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            grid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 310);
            grid.ColumnSpacing = compact ? 0 : 14;
            Grid.SetColumn(sidebar, compact ? 0 : 1);
            stage.Visibility = lens ? Visibility.Collapsed : Visibility.Visible;
            sidebar.Visibility = !compact || lens ? Visibility.Visible : Visibility.Collapsed;
        }
        grid.SizeChanged += (_, _) => Reflow(); lensToggle.Checked += (_, _) => Reflow(); lensToggle.Unchecked += (_, _) => Reflow();

        vectorMode.SelectionChanged += (_, _) => { colorBy.Visibility = vectorMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; mapViewport.ChangeView(0, 0, 1); ResizeVectorStage(); SaveWorkspaceSoon(); };
        colorBy.SelectionChanged += (_, _) => { if (!populatingColors) { DrawMap(); SaveWorkspaceSoon(); } };
        vectorColumn.SelectionChanged += (_, _) => { vectorSource = null; vectorModel.ItemsSource = null; ResetVectorExploration(); refreshVectorChrome?.Invoke(); };
        vectorModel.SelectionChanged += (_, _) =>
        {
            if (vectorModel.SelectedItem is VectorScope scope) ToolTipService.SetToolTip(vectorModel, $"Model: {scope.Model ?? "unspecified"}\nRevision: {scope.Revision ?? "unspecified"}\nPipeline: {scope.Pipeline ?? "unspecified"}\nUp to 100 spaces discovered; Explore loads the selected space.");
            if (!populatingScope) { ResetVectorExploration(); SaveWorkspaceSoon(); }
        };
        vectorSearch.TextChanged += (_, _) => SaveWorkspaceSoon();
        vectorSearch.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { MapClick(this, new()); e.Handled = true; } };
        mapViewport.SizeChanged += (_, _) => ResizeVectorStage();
        map.SizeChanged += (_, _) => DrawMap(); map.ActualThemeChanged += (_, _) => DrawMap(); fingerprint.SizeChanged += (_, _) => DrawFingerprint();
        map.PointerPressed += MapPointerPressed; map.PointerMoved += MapPointerMoved; map.PointerReleased += MapPointerReleased;
        AutomationProperties.SetHelpText(map, "Constellation: select a row to hop through its cosine neighborhood. PCA: Shift+drag selects a region. Ctrl+wheel zooms.");

        // Explore and Health need a column; say so instead of leaving them greyed out without a reason.
        refreshVectorChrome = () =>
        {
            bool hasColumn = db != null && vectorColumn.SelectedItem is VectorColumn;
            explore.IsEnabled = health.IsEnabled = hasColumn && operation == null;
            vectorSourcesButton.IsEnabled = vectorSavedButton.IsEnabled = db != null;
            vectorColumn.Visibility = explore.Visibility = health.Visibility = vectorSavedButton.Visibility = hasColumn ? Visibility.Visible : Visibility.Collapsed;
            // Most columns hold one set of vectors. The picker only appears when the table records several
            // (different model, revision or pipeline values beside the vectors).
            vectorModel.Visibility = hasColumn && vectorModel.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            bool onSources = vectorSourcesSurface?.Visibility == Visibility.Visible, onMap = vectorExplorerSurface?.Visibility == Visibility.Visible;
            vectorSourcesButton.Visibility = hasColumn && !onSources ? Visibility.Visible : Visibility.Collapsed;
            backToMap.Visibility = hasColumn && !onMap && vectorItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            sourceBar.Visibility = hasColumn ? Visibility.Visible : Visibility.Collapsed;
            sourceNote.Text = hasColumn && onSources ? "Vector columns in this database. Open one to map it." : "";
            sourceNote.Visibility = sourceNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        var header = PageHeader("Vector lab", "Map a stored vector column, hop between a row's nearest neighbours and check coverage. Every query here is read-only.", Pill("Read-only"));
        var page = new Grid { RowSpacing = 10 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) page.RowDefinitions.Add(new() { Height = height });
        page.Children.Add(header);
        Grid.SetRow(sourceBar, 1); page.Children.Add(sourceBar);
        Grid.SetRow(sourceNote, 2); page.Children.Add(sourceNote);
        var surfaces = VectorSurfaces(grid);
        Grid.SetRow(surfaces, 3); page.Children.Add(surfaces);
        refreshVectorChrome();
        return page;
    }
    void ResizeVectorStage()
    {
        map.Width=Math.Max(420,mapViewport.ActualWidth-4);
        map.Height=Math.Max(vectorMode.SelectedIndex==2?540:400,mapViewport.ActualHeight-4);
        DrawMap();
    }
    void ResetVectorExploration()
    {
        vectorGeneration++;vectorAnchor=null;
        vectorTrail.Clear();if(vectorBack!=null)vectorBack.IsEnabled=false;
        points=[];vectorItems=[];projection=null;selectedPoints.Clear();comparisonReference=null;focusedVector=0;pointMetadata.Clear();vectorMeaning.Text="";
        vectorInput.Text="";vectorNeighbors.Children.Clear();fingerprint.Children.Clear();vectorFocus.Text="Choose a column, then Explore";vectorDetails.Text="Words and labels are resolved through declared foreign keys. All Vector Lab queries use server-enforced read-only transactions.";
        vectorHealth.Text="No sample loaded";mapCaption.Text="Nothing loaded yet";vectorLegend.Text="";RefreshComparison();DrawMap();RefreshVectorExperimentActions();
    }
    async Task PresentVectors(List<VectorItem> rows,CancellationToken ct,string origin)
    {
        int generation=vectorGeneration;
        var projected=await Task.Run(()=>Embeddings.Pca(rows.Select(r=>r.Values).ToArray(),ct),ct);ct.ThrowIfCancellationRequested();
        if(generation!=vectorGeneration)throw new OperationCanceledException();
        vectorItems=rows;points=rows.Select(r=>new PointData(r.Metadata,r.Values)).ToList();projection=projected;selectedPoints.Clear();focusedVector=0;
        vectorTrail.Clear();if(vectorBack!=null)vectorBack.IsEnabled=false;
        pointMetadata.Clear();foreach(var point in points)pointMetadata[point]=EmbeddingMetadata.Flatten(point.Metadata);
        vectorLabelCounts=rows.GroupBy(r=>r.Label).ToDictionary(g=>g.Key,g=>g.Count());
        populatingColors=true;var fields=pointMetadata.Values.SelectMany(r=>r.Keys).Distinct().Where(k=>pointMetadata.Values.Select(r=>r.GetValueOrDefault(k,"NULL")).Distinct().Take(21).Count()<=20).ToList();
        colorBy.ItemsSource=new[]{"Single color"}.Concat(fields).ToList();colorBy.SelectedIndex=0;populatingColors=false;
        var norms=rows.Select(r=>Embeddings.Norm(r.Values)).ToArray();
        vectorHealth.Text=rows.Count==0?"No matches. Try a shorter label.":$"{rows.Count:N0} rows · {projected.Dimensions} dimensions · {norms.Count(n=>n==0)} zero vectors · {vectorSource?.Description}";
        mapCaption.Text=origin;vectorEvidence.Text=$"Read-only · projected on this computer · PCA retains {projected.Explained:P1} of the variance";
        if(rows.Count>0)FocusVector(0);else{vectorNeighbors.Children.Clear();vectorFocus.Text="No matches";vectorMeaning.Text="";vectorInput.Text="";comparisonReference=null;RefreshComparison();SaveWorkspaceSoon();fingerprint.Children.Clear();vectorDetails.Text="No stored vectors matched this search in the selected embedding space.";DrawMap();}
        status.Text="Vector lab ready · select a row, pin a reference, or search database neighbors";
    }
    void FocusVector(int index,bool recordHistory=true)
    {
        if(index<0||index>=points.Count)return;
        if(recordHistory&&index!=focusedVector){if(vectorTrail.Count>=50)vectorTrail.Clear();vectorTrail.Push(focusedVector);}
        if(vectorBack!=null)vectorBack.IsEnabled=vectorTrail.Count>0;
        focusedVector=index;selectedPoints.Clear();selectedPoints.Add(index);vectorInput.Text=Embeddings.Serialize(points[index].Values);InspectPoint(points[index]);DrawMap();SaveWorkspaceSoon();
    }
    void UpdateVectorLens(PointData point)
    {
        if(focusedVector>=vectorItems.Count)return;
        // PCA region selection may choose a different row than the previous focus.
        int index=points.IndexOf(point);if(index>=0)focusedVector=index;
        vectorFocus.Text=VectorDisplayLabel(focusedVector);
        var metadata=pointMetadata.GetValueOrDefault(point,[]);
        vectorMeaning.Text=new[]{"sense_text","linked.definition","definition","body"}.Select(k=>metadata.GetValueOrDefault(k)).FirstOrDefault(v=>!string.IsNullOrWhiteSpace(v)&&v!="NULL")??"";
        ToolTipService.SetToolTip(vectorMeaning,vectorMeaning.Text);
        vectorNeighbors.Children.Clear();
        var neighbors=VectorLens.Neighbors(points.Select(p=>p.Values).ToArray(),point.Values,13).Where(n=>n.Index!=focusedVector).Take(12);
        foreach(var n in neighbors)
        {
            int target=n.Index;var item=vectorItems[target];var body=new StackPanel{Spacing=3};body.Children.Add(Text(VectorDisplayLabel(target),13));body.Children.Add(Text($"cos {n.Similarity:0.0000}"+(item.Distance is double d?$" · DB distance {d:0.0000}":""),11));
            var button=new Button{Content=body,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};AutomationProperties.SetName(button,$"Focus {VectorDisplayLabel(target)}, cosine {n.Similarity:0.0000}");button.Click+=(_,_)=>FocusVector(target);vectorNeighbors.Children.Add(button);
        }
        DrawFingerprint();RefreshComparison();
    }
    void PinVectorReference(){if(focusedVector>=vectorItems.Count||selectedPoints.Count!=1)return;comparisonReference=vectorItems[focusedVector];RefreshComparison();DrawFingerprint();SaveWorkspaceSoon();}
    void RefreshComparison()
    {
        if(comparisonReference==null||focusedVector>=vectorItems.Count){vectorComparison.Text="Pin a reference, then select another row to compare.";return;}
        var current=vectorItems[focusedVector];double cosine=Embeddings.Cosine(comparisonReference.Values,current.Values);double l2=Math.Sqrt(current.Values.Zip(comparisonReference.Values).Sum(p=>Math.Pow(p.First-p.Second,2)));
        vectorComparison.Text=$"PINNED · {comparisonReference.Label}\n↔ {current.Label}\nCosine {(double.IsFinite(cosine)?cosine.ToString("0.00000"):"undefined (zero vector)")} · L2 {l2:0.00000}\nExact loaded components; similarity is not a probability.";
    }
    void DrawFingerprint()
    {
        fingerprint.Children.Clear();if(focusedVector>=vectorItems.Count)return;
        var values=VectorLens.Fingerprint(vectorItems[focusedVector].Values);var reference=comparisonReference==null?null:VectorLens.Fingerprint(comparisonReference.Values);
        double max=values.Concat(reference??[]).Select(Math.Abs).DefaultIfEmpty(0).Max();if(max==0)max=1;
        double width=Math.Max(220,fingerprint.ActualWidth),cell=width/values.Length;
        for(int row=0;row<(reference==null?1:2);row++)for(int i=0;i<values.Length;i++)
        {
            double value=(row==0?values:reference!)[i];var tile=new Rectangle{Width=Math.Max(1,cell-1),Height=reference==null?46:25,RadiusX=1,RadiusY=1,Fill=new SolidColorBrush(value>=0?pointColors[0]:pointColors[1]),Opacity=.18+.82*Math.Abs(value)/max};Canvas.SetLeft(tile,i*cell);Canvas.SetTop(tile,row*29);
            ToolTipService.SetToolTip(tile,$"{(row==0?"Focus":"Reference")} band {i+1}/{values.Length}: mean {value:0.00000}");fingerprint.Children.Add(tile);
        }
    }
    /// <summary>Shown on the map before a sample is loaded.</summary>
    void DrawVectorEmpty()
    {
        var message = Display("Nothing loaded yet", 22);
        Canvas.SetLeft(message, 28); Canvas.SetTop(message, 36); map.Children.Add(message);
        var detail = Muted("Choose a column, then Explore. Search real labels and follow a row to its nearest neighbours.\nYour database stays read-only.", 14);
        detail.MaxWidth = 520;
        Canvas.SetLeft(detail, 28); Canvas.SetTop(detail, 74); map.Children.Add(detail);
    }
    string VectorDisplayLabel(int index)
    {
        var label=vectorItems[index].Label;if(vectorLabelCounts.GetValueOrDefault(label)<=1)return label;
        var fields=pointMetadata.GetValueOrDefault(points[index]);
        return fields?.GetValueOrDefault("sense_index") is string sense&&sense!="NULL"?label+" · sense "+sense:label+" · row "+(index+1);
    }
    void DrawConstellation()
    {
        if(focusedVector>=points.Count)return;
        double w=Math.Max(300,map.ActualWidth),h=Math.Max(260,map.ActualHeight),cx=w/2,cy=h/2;var seed=points[focusedVector].Values;
        var near=VectorLens.Neighbors(points.Select(p=>p.Values).ToArray(),seed,7).Where(n=>n.Index!=focusedVector).Take(6).ToList();
        vectorLegend.Text="COSINE LINKS · positions separate labels; scores carry similarity";
        double radius=Math.Max(65,Math.Min(w,h)/2-68);
        for(int ring=1;ring<=3;ring++){double r=radius*ring/3;var halo=new Ellipse{Width=r*2,Height=r*2,Stroke=new SolidColorBrush(pointColors[1]),StrokeThickness=1,Opacity=.16};Canvas.SetLeft(halo,cx-r);Canvas.SetTop(halo,cy-r);map.Children.Add(halo);}
        for(int j=0;j<near.Count;j++)
        {
            var n=near[j];double angle=-Math.PI/2+j*2*Math.PI/near.Count;
            double x=cx+Math.Cos(angle)*Math.Max(120,w/2-72),y=cy+Math.Sin(angle)*Math.Max(110,h/2-60);
            var line=new Line{X1=cx,Y1=cy,X2=x,Y2=y,Stroke=new SolidColorBrush(pointColors[1]),StrokeThickness=1+Math.Max(0,n.Similarity)*2,Opacity=.32};map.Children.Add(line);
            AddConstellationNode(n.Index,x,y,false,$"{n.Similarity:0.000}");
        }
        AddConstellationNode(focusedVector,cx,cy,true,"FOCUS");
        var caption=Text("Nearest 6 in loaded rows · select a row to recenter",10);Canvas.SetLeft(caption,12);Canvas.SetTop(caption,h-22);map.Children.Add(caption);
    }
    void DrawSimilarityMatrix()
    {
        if(focusedVector>=points.Count)return;
        var neighbors=VectorLens.Neighbors(points.Select(p=>p.Values).ToArray(),points[focusedVector].Values,16);
        if(neighbors.Count==0){vectorLegend.Text="Cosine matrix unavailable for zero vectors. Select a nonzero embedding in PCA atlas.";return;}
        vectorLegend.Text="PAIRWISE COSINE · focus neighborhood, up to 16 rows · amber positive / teal negative · select a cell to compare";
        double cell=Math.Clamp((map.ActualWidth-165)/neighbors.Count,18,30),left=135,top=28;
        for(int i=0;i<neighbors.Count;i++)
        {
            int rowIndex=neighbors[i].Index;var label=Text($"{i+1:00}  {VectorDisplayLabel(rowIndex)}",11);label.Width=125;label.MaxHeight=cell;label.TextTrimming=TextTrimming.CharacterEllipsis;Canvas.SetLeft(label,4);Canvas.SetTop(label,top+i*cell+3);map.Children.Add(label);
            var number=Text((i+1).ToString("00"),10);Canvas.SetLeft(number,left+i*cell+4);Canvas.SetTop(number,5);map.Children.Add(number);
            for(int j=0;j<neighbors.Count;j++)
            {
                int colIndex=neighbors[j].Index;double cosine=Embeddings.Cosine(points[rowIndex].Values,points[colIndex].Values);
                var tile=new Button{Width=cell-2,Height=cell-2,MinWidth=0,MinHeight=0,Padding=new Thickness(0),CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(cosine>=0?pointColors[0]:pointColors[1]),Opacity=.2+.8*Math.Abs(cosine),BorderThickness=new Thickness(i==j?1:0),BorderBrush=new SolidColorBrush(Microsoft.UI.Colors.White)};
                string description=$"{VectorDisplayLabel(rowIndex)} ↔ {VectorDisplayLabel(colIndex)}: cosine {cosine:0.00000}";ToolTipService.SetToolTip(tile,description);AutomationProperties.SetName(tile,description);
                tile.Click+=(_,_)=>{comparisonReference=vectorItems[colIndex];FocusVector(rowIndex);};Canvas.SetLeft(tile,left+j*cell);Canvas.SetTop(tile,top+i*cell);map.Children.Add(tile);
            }
        }
        var note=Text("Exact component comparisons within the loaded neighborhood.\nEach cell opens an A/B comparison in the focus lens; no projection involved.",12);Canvas.SetLeft(note,16);Canvas.SetTop(note,top+neighbors.Count*cell+18);map.Children.Add(note);
    }
    /// <summary>One labelled node in the constellation. Nodes are dark in both themes, so their text is always light.</summary>
    void AddConstellationNode(int index, double x, double y, bool focus, string score)
    {
        const double width = 150, height = 54;
        var light = new SolidColorBrush(Brand.Paper);
        var name = new TextBlock { Text = VectorDisplayLabel(index), FontSize = focus ? 15 : 13, FontWeight = focus ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, MaxWidth = width - 20, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        var sub = new TextBlock { Text = score, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(name); panel.Children.Add(sub);
        var button = new Button
        {
            Content = panel, Width = width, Height = height, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(focus ? 2 : 1), BorderBrush = new SolidColorBrush(focus ? pointColors[0] : pointColors[1])
        };
        if (!HighContrast)
        {
            // The fill and text are fixed for every pointer state so a node stays readable while hovered or pressed.
            var fill = new SolidColorBrush(focus ? Brand.Teal : Brand.Ink);
            foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" }) button.Resources[key] = fill;
            foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" }) button.Resources[key] = light;
            button.Background = fill; button.Foreground = light; name.Foreground = sub.Foreground = light;
        }
        AutomationProperties.SetName(button, $"{(focus ? "Focused" : "Focus")} {VectorDisplayLabel(index)}, {score}");
        ToolTipService.SetToolTip(button, VectorDisplayLabel(index));
        button.Click += (_, _) => FocusVector(index);
        Canvas.SetLeft(button, Math.Clamp(x - width / 2, 0, Math.Max(0, map.ActualWidth - width)));
        Canvas.SetTop(button, y - height / 2);
        map.Children.Add(button);
        pointPositions[index] = new Point(x, y);
    }
}
