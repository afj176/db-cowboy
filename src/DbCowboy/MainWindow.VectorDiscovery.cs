using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

public sealed partial class MainWindow
{
    readonly StackPanel vectorCards = Fluent.ArrowKeys(Fluent.Stagger(new StackPanel { Spacing=10 })), vectorHealthBody = new() { Spacing=14 };
    readonly Button vectorSourcesButton = new() { Content="Sources" }, vectorLabelsButton = new() { Content="Labels · automatic" }, vectorSavedButton = new() { Content="Views" };
    readonly Dictionary<VectorColumn,VectorSource> vectorSources = [];
    FrameworkElement? vectorExplorerSurface,vectorSourcesSurface,vectorHealthSurface;
    VectorInvestigation? vectorRestore;
    VectorBookmark? vectorAnchor;
    bool vectorRestoring;

    /// <summary>The three Vector lab surfaces share one area: the column list, the map with its lens, and the health report.</summary>
    Grid VectorSurfaces(FrameworkElement explorer)
    {
        vectorExplorerSurface = explorer;
        vectorSourcesSurface = new ScrollViewer { Content = LeftCapped(vectorCards, 1040), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 8) };
        vectorHealthSurface = new ScrollViewer { Content = LeftCapped(vectorHealthBody, 1040), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 8) };
        var host = new Grid();
        host.Children.Add(explorer); host.Children.Add(vectorSourcesSurface); host.Children.Add(vectorHealthSurface);
        AddVectorDisconnectedState();
        vectorSourcesButton.Click += (_, _) => { SaveWorkspace(); ShowVectorSurface("sources"); };
        vectorSavedButton.Click += (_, _) => ShowSavedVectorViews();
        vectorLabelsButton.Click += (_, _) => ChooseVectorLabel();
        ShowVectorSurface("sources");
        return host;
    }

    void AddVectorDisconnectedState()
    {
        vectorCards.Children.Add(Surface(EmptyState("02-wait-what", "No database connected", "Connect to Postgres to find its vector and halfvec columns. Nothing in this lab leaves the database.", Button("Connections", ConnectionsClick, true)), 0));
    }

    void ShowVectorSurface(string surface)
    {
        if (vectorExplorerSurface != null) vectorExplorerSurface.Visibility = surface == "explore" ? Visibility.Visible : Visibility.Collapsed;
        if (vectorSourcesSurface != null) vectorSourcesSurface.Visibility = surface == "sources" ? Visibility.Visible : Visibility.Collapsed;
        if (vectorHealthSurface != null) vectorHealthSurface.Visibility = surface == "health" ? Visibility.Visible : Visibility.Collapsed;
        refreshVectorChrome?.Invoke();
    }

    /// <summary>One vector column as a card: where it lives, its type and size, and how its points get their labels.</summary>
    Button VectorSourceCard(VectorColumn vector, VectorSource source, string estimate, bool resume)
    {
        var name = Display(vector.Schema + "." + vector.Table, 18);
        var pills = Row(Pill(vector.Column, Tone.Accent), Pill(source.DeclaredType ?? vector.Type), Pill(estimate));
        pills.Spacing = 6;
        var detail = Muted(source.Description, 13);
        var go = Text(resume ? "Continue your investigation  →" : "Explore this column  →", 13);
        go.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(name); body.Children.Add(pills); body.Children.Add(detail); body.Children.Add(go);
        var card = new Button { Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(18, 16, 18, 18), CornerRadius = new CornerRadius(12) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"Explore {vector.Schema}.{vector.Table}.{vector.Column}, {source.DeclaredType}, {source.Description}");
        return Themed(card, (c, dark) =>
        {
            if (HighContrast) return;
            c.Background = new SolidColorBrush(dark ? Brand.Raised : Microsoft.UI.Colors.White);
            go.Foreground = new SolidColorBrush(ToneColor(Tone.Accent, dark));
        });
    }

    async Task RefreshVectorSources(IReadOnlyList<VectorColumn> vectors,CancellationToken ct,IReadOnlyDictionary<VectorColumn,VectorSource>? prepared=null)
    {
        vectorSources.Clear();vectorCards.Children.Clear();vectorRestore=null;vectorAnchor=null;
        vectorSourcesButton.Content=$"All columns ({vectors.Count})";ShowVectorSurface("sources");
        if(vectors.Count==0)vectorCards.Children.Add(Surface(EmptyState("02-wait-what","No vector columns here","No vector or halfvec columns are visible to this connection. Add one, then refresh the schema.",Button("Refresh schema",RefreshClick)),0));
        foreach(var vector in vectors.Take(12))
        {
            var source=prepared!=null&&prepared.TryGetValue(vector,out var ready)?ready:await Connected().VectorSource(vector,ct);vectorSources[vector]=source;
            var relation=relations.FirstOrDefault(r=>r.Schema==vector.Schema&&r.Name==vector.Table);
            string estimate=relation?.Estimate>=0?$"≈ {relation.Estimate:N0} rows":"Row count not estimated";
            var last=workspace.VectorLast.GetValueOrDefault(Connected().Profile.Id);
            bool resume=last!=null&&last.Schema==vector.Schema&&last.Table==vector.Table&&last.Column==vector.Column;
            var card=VectorSourceCard(vector,source,estimate,resume);
            card.Click+=async(_,_)=>{var latest=workspace.VectorLast.GetValueOrDefault(Connected().Profile.Id);await OpenVectorInvestigation(vector,latest?.Schema==vector.Schema&&latest.Table==vector.Table&&latest.Column==vector.Column?latest:null);};vectorCards.Children.Add(Fluent.Lift(card));
        }
        if(vectors.Count>12)vectorCards.Children.Add(Muted("Showing the first 12 columns. Every discovered column is in the column picker above.",13));
        if(workspace.VectorLast.TryGetValue(Connected().Profile.Id,out var saved))
        {
            var selected=vectors.FirstOrDefault(v=>v.Schema==saved.Schema&&v.Table==saved.Table&&v.Column==saved.Column);
            if(selected!=null){vectorColumn.SelectedItem=selected;vectorRestore=saved;ApplyVectorSettings(saved);}
        }
    }
    void ApplyVectorSettings(VectorInvestigation saved)
    {
        vectorRestoring=true;try{vectorSearch.Text=saved.Search;vectorMode.SelectedIndex=Math.Clamp(saved.Mode,0,2);metric.SelectedIndex=Math.Clamp(saved.Metric,0,2);}finally{vectorRestoring=false;}
    }
    async Task OpenVectorInvestigation(VectorColumn vector,VectorInvestigation? saved)
    {
        if(operation!=null)return;vectorRestoring=true;
        try{vectorColumn.SelectedItem=vector;ResetVectorExploration();vectorRestore=saved;vectorSearch.Text=saved?.Search??"";if(saved!=null)ApplyVectorSettings(saved);}finally{vectorRestoring=false;}
        await Work(LoadVectorInvestigation,vectorReadOnly:true);
    }
    async Task LoadVectorInvestigation(CancellationToken ct)
    {
        var vector=vectorColumn.SelectedItem as VectorColumn??throw new InvalidOperationException("Choose an embedding source after connecting.");
        var restore=vectorRestore;vectorRestore=null;
        bool changedSpace=false;
        if(restore!=null&&(restore.Schema!=vector.Schema||restore.Table!=vector.Table||restore.Column!=vector.Column))restore=null;
        if(vectorSource?.Vector!=vector||restore?.Label!=null)
        {
            vectorSource=vectorSources.TryGetValue(vector,out var cached)&&(restore?.Label==null||restore.Label==cached.LabelKey)?cached:await Connected().VectorSource(vector,ct,restore?.Label);
            var scopes=await Connected().VectorScopes(vectorSource,ct);populatingScope=true;
            if(restore?.Scope!=null&&!scopes.Contains(restore.Scope)){restore=restore with{Scope=scopes.FirstOrDefault(),Focus=null,Pin=null,Anchor=null};changedSpace=true;}
            try{vectorModel.ItemsSource=scopes;vectorModel.SelectedItem=scopes.FirstOrDefault(s=>s==restore?.Scope)??scopes.FirstOrDefault();}finally{populatingScope=false;}
        }
        var source=vectorSource;vectorLabelsButton.Content="Labels · "+source.Labels?.FirstOrDefault(l=>l.Key==source.LabelKey)?.Title;
        var scope=vectorModel.SelectedItem as VectorScope;
        if(scope==null){ShowVectorSurface("explore");throw new InvalidOperationException("This source has no non-NULL embeddings. Health can show its missing-vector coverage.");}
        VectorItem? anchor=null;
        if(VectorInvestigationState.Compatible(source,restore?.Anchor))anchor=(await Connected().VectorRows(source,scope,"",null,0,ct,restore!.Anchor)).FirstOrDefault();
        var rows=await Connected().VectorRows(source,scope,anchor==null?vectorSearch.Text.Trim():"",anchor?.Values,metric.SelectedIndex,ct);
        if(vectorSource!=source||vectorColumn.SelectedItem as VectorColumn!=vector||vectorModel.SelectedItem as VectorScope!=scope)throw new OperationCanceledException();
        await PresentVectors(rows,ct,anchor==null?"Stable sample · up to 500 rows":$"Restored exact neighbors of {anchor.Label}");
        vectorRestoring=true;try
        {
            vectorAnchor=anchor==null?null:restore?.Anchor;
            if(VectorInvestigationState.Compatible(source,restore?.Pin))comparisonReference=(await Connected().VectorRows(source,scope,"",null,0,ct,restore!.Pin)).FirstOrDefault();
            if(restore?.Focus!=null){int focus=vectorItems.FindIndex(r=>VectorInvestigationState.Matches(source,restore.Focus,r));if(focus>=0)FocusVector(focus,false);}
            if(restore!=null&&colorBy.ItemsSource is IEnumerable<string> colors&&colors.Contains(restore.Color))colorBy.SelectedItem=restore.Color;
            RefreshComparison();DrawFingerprint();
        }finally{vectorRestoring=false;}
        ShowVectorSurface("explore");SaveWorkspaceSoon();
        if(changedSpace)status.Text="The saved vector set is no longer in this column · opened the first one without its old pins";
    }
    VectorInvestigation? CaptureVectorInvestigation()
    {
        if(vectorSource==null||vectorColumn.SelectedItem as VectorColumn!=vectorSource.Vector||vectorModel.SelectedItem is not VectorScope scope)return null;
        var v=vectorSource.Vector;var focus=focusedVector<vectorItems.Count?vectorItems[focusedVector]:null;
        return new(v.Schema,v.Table,v.Column,scope,vectorSearch.Text,vectorMode.SelectedIndex,metric.SelectedIndex,colorBy.SelectedItem as string??"Single color",vectorSource.LabelKey,VectorInvestigationState.Bookmark(vectorSource,focus),VectorInvestigationState.Bookmark(vectorSource,comparisonReference),vectorAnchor,v.Table+" · "+(string.IsNullOrWhiteSpace(vectorSearch.Text)?focus?.Label??"sample":vectorSearch.Text)+" · "+scope);
    }
    void RememberVectorInvestigation(){if(!vectorRestoring&&db!=null&&CaptureVectorInvestigation() is {} saved)workspace.VectorLast[db.Profile.Id]=saved;}
    void SaveVectorInvestigation()
    {
        if(operation!=null){status.Text="Wait for this read to finish before saving its view.";return;}
        if(db==null||CaptureVectorInvestigation() is not {} saved){status.Text="Explore a source before saving a view.";return;}
        var list=workspace.VectorSaved.GetValueOrDefault(db.Profile.Id,[]);list.RemoveAll(v=>v.Name==saved.Name&&v.Schema==saved.Schema&&v.Table==saved.Table&&v.Column==saved.Column);list.Insert(0,saved);workspace.VectorSaved[db.Profile.Id]=list.Take(12).ToList();SaveWorkspace();status.Text=comparisonReference!=null&&saved.Pin==null?"View saved locally · this pin needs a primary key to be restored":"Investigation saved locally · no embedding arrays stored";
    }
    void ShowSavedVectorViews()
    {
        var menu=Fluent.Acrylic(new MenuFlyout());var saved=db==null?[]:workspace.VectorSaved.GetValueOrDefault(db.Profile.Id,[]);
        var save=new MenuFlyoutItem{Text="Save this investigation",IsEnabled=db!=null&&vectorSource!=null&&operation==null};save.Click+=(_,_)=>SaveVectorInvestigation();menu.Items.Add(save);menu.Items.Add(new MenuFlyoutSeparator());
        foreach(var view in saved){var item=new MenuFlyoutItem{Text=view.Name};item.Click+=async(_,_)=>{var vector=(vectorColumn.ItemsSource as IEnumerable<VectorColumn>)?.FirstOrDefault(v=>v.Schema==view.Schema&&v.Table==view.Table&&v.Column==view.Column);if(vector!=null)await OpenVectorInvestigation(vector,view);else status.Text="This saved source is unavailable in the current schema.";};menu.Items.Add(item);}
        if(saved.Count==0)menu.Items.Add(new MenuFlyoutItem{Text="Saved investigations appear here",IsEnabled=false});
        menu.Items.Add(new MenuFlyoutSeparator());var reset=new MenuFlyoutItem{Text="Reset zoom"};reset.Click+=(_,_)=>mapViewport.ChangeView(0,0,1);menu.Items.Add(reset);menu.ShowAt(vectorSavedButton);
    }
    void ChooseVectorLabel()
    {
        if(vectorSource==null)return;var menu=Fluent.Acrylic(new MenuFlyout());
        foreach(var choice in (vectorSource.Labels??[]).OrderBy(c=>c.Key==vectorSource.LabelKey?0:new[]{"word","title","name","label","sense_text","content","text","description","caption","body"}.Contains(c.Key[(c.Key.IndexOf(':')+1)..])?1:2).ThenBy(c=>c.Title)){var item=new MenuFlyoutItem{Text=(choice.Key==vectorSource.LabelKey?"✓ ":"")+choice.Title};item.Click+=async(_,_)=>{if(operation!=null||CaptureVectorInvestigation() is not {} saved)return;await OpenVectorInvestigation(vectorSource.Vector,saved with{Label=choice.Key,Anchor=null,Focus=null,Pin=null});};menu.Items.Add(item);}menu.ShowAt(vectorLabelsButton);
    }
    async void VectorHealthClick(object sender,RoutedEventArgs e)=>await Work(async ct=>
    {
        var vector=vectorColumn.SelectedItem as VectorColumn??throw new InvalidOperationException("Connect and choose an embedding source first.");
        var source=vectorSource?.Vector==vector?vectorSource:await Connected().VectorSource(vector,ct);
        var scope=vectorModel.SelectedItem as VectorScope;
        if(scope==null){var spaces=await Connected().VectorScopes(source,ct);scope=spaces.FirstOrDefault(s=>s==vectorRestore?.Scope)??spaces.FirstOrDefault();}
        var report=await Connected().VectorHealth(source,scope,ct);if(vectorColumn.SelectedItem as VectorColumn!=vector)throw new OperationCanceledException();
        vectorHealthBody.Children.Clear();
        vectorHealthBody.Children.Add(Display("Column health", 22));
        vectorHealthBody.Children.Add(Muted($"{vector.Schema}.{vector.Table} · {vector.Column} · read-only snapshot. Counts are live reads, so concurrent writes can change them between queries.", 13));
        long coverageRows = report.Linked?.Rows ?? report.Rows, covered = report.Linked?.Covered ?? report.Embedded;
        long missing = coverageRows - covered;
        var norms = report.Sample.Select(r => Embeddings.Norm(r.Values)).ToList();
        string dimensions = string.Join(" · ", report.Sample.GroupBy(r => r.Values.Length).Select(g => $"{g.Key}D: {g.Count():N0}"));
        vectorHealthBody.Children.Add(Flow(190,
            Tile("Coverage", coverageRows == 0 ? "–" : ((double)covered / coverageRows).ToString("P1"), out _, report.Linked == null ? "rows that have a vector" : report.Linked.Relation + " · selected space"),
            Tile("Missing", missing.ToString("N0"), out _, report.Linked == null ? "NULL vectors" : "linked rows without a vector here"),
            Tile("Zero vectors", norms.Count == 0 ? "–" : norms.Count(n => n == 0).ToString("N0"), out _, $"in a sample of {norms.Count:N0}"),
            Tile("Norm range", norms.Count == 0 ? "–" : $"{norms.Min():0.000}–{norms.Max():0.000}", out _, "in the same sample")));
        if (report.Linked is { } linked)
            vectorHealthBody.Children.Add(Muted($"{linked.Covered:N0} of {linked.Rows:N0} rows in {linked.Relation} have at least one vector here. Several vectors for one row count once.", 13));
        var facts = MiniTable(["Scope", "What was read"],
        [
            ["Whole column", $"{report.Rows:N0} rows · {report.Embedded:N0} with a vector · {report.Rows - report.Embedded:N0} NULL"],
            ["Sample · " + scope, $"First {report.Sample.Count:N0} rows with a vector, up to 500 · {dimensions}. A sample check is not a whole-table audit."]
        ], [1, 3]);
        vectorHealthBody.Children.Add(Surface(facts, 18));
        // Each space is a bar sized by its row count; a space with vectors opens in the map.
        var spaceBars = report.Spaces.Select(space => (
            Label: space.Scope.ToString(),
            Value: (double)space.Rows,
            Display: $"{space.Rows:N0} rows",
            Detail: (string?)($"{space.Embedded:N0} with a vector · {space.Rows - space.Embedded:N0} NULL" + (space.Embedded > 0 ? "" : " · nothing to map")),
            Open: space.Embedded > 0 ? (Action?)(async () => await OpenVectorInvestigation(vector, (CaptureVectorInvestigation() ?? new(vector.Schema, vector.Table, vector.Column)) with { Scope = space.Scope, Search = "", Focus = null, Pin = null, Anchor = null, Label = source.LabelKey })) : null)).ToList();
        // Only worth showing when the table really holds more than one set of vectors.
        if (spaceBars.Count > 1)
            vectorHealthBody.Children.Add(Surface(Stack(Display("Vector sets in this column", 17), Muted("Rows grouped by the model, revision and pipeline values stored beside each vector. Up to 100, by row count. Open one to map it.", 13), Bars(spaceBars, Tone.Good)), 18));
        ShowVectorSurface("health");(vectorHealthSurface as ScrollViewer)?.ChangeView(0,0,1);status.Text="Health snapshot ready · no database objects or rows changed";
    },vectorReadOnly:true);
}
