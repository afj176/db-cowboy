using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace DbCowboy;

public sealed partial class MainWindow
{
    readonly Button vectorCompareButton=new(){Content="Compare pipelines",IsEnabled=false},vectorQualityButton=new(){Content="Check search",IsEnabled=false};
    void RefreshVectorExperimentActions()
    {
        bool ready=operation==null&&vectorSource!=null&&vectorItems.Count>0&&vectorModel.SelectedItem is VectorScope;
        vectorCompareButton.IsEnabled=vectorQualityButton.IsEnabled=ready;
    }

    (VectorSource Source,VectorScope Scope,VectorItem Anchor) VectorExperimentContext()
    {
        if(vectorSource==null||vectorColumn.SelectedItem as VectorColumn!=vectorSource.Vector||vectorModel.SelectedItem is not VectorScope scope||vectorItems.Count==0||focusedVector>=vectorItems.Count)
            throw new InvalidOperationException("Explore a source and select a record first.");
        return(vectorSource,scope,vectorItems[focusedVector]);
    }
    void ExperimentHeader(string title,string subtitle)
    {
        vectorHealthBody.Children.Clear();vectorHealthBody.Children.Add(Row(Button("← Back to exploration",(_,_)=>ShowVectorSurface("explore")),Text("Read-only · every number here comes from Postgres",11)));
        vectorHealthBody.Children.Add(Text(title,24));vectorHealthBody.Children.Add(Text(subtitle,13));ShowVectorSurface("health");(vectorHealthSurface as ScrollViewer)?.ChangeView(0,0,1);
    }
    static string ExperimentLabel(VectorItem row)
    {
        using var doc=JsonDocument.Parse(row.Metadata);
        return row.Label+(doc.RootElement.TryGetProperty("sense_index",out var sense)?" · sense "+sense.ToString():"");
    }
    static string CounterpartCaption(VectorItem row)
    {
        using var doc=JsonDocument.Parse(row.Metadata);
        string? snippet=new[]{"sense_text","content","text","body","caption"}.Select(k=>doc.RootElement.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null).FirstOrDefault(v=>!string.IsNullOrWhiteSpace(v));
        return ExperimentLabel(row)+(snippet==null?"":" · "+snippet[..Math.Min(100,snippet.Length)].Replace('\n',' '));
    }
    async void ComparePipelinesClick(object sender,RoutedEventArgs e)=>await Work(async ct=>
    {
        var (source,scope,anchor)=VectorExperimentContext();
        var spaces=(await Connected().VectorScopes(source,ct)).Where(s=>s!=scope).ToArray();
        ExperimentHeader("Compare pipelines",ExperimentLabel(anchor)+" · compare neighbor rankings using each space’s own embedding");
        vectorHealthBody.Children.Add(Text("Records match by "+(source.Context==null?"primary key excluding model/revision/pipeline":"declared relationship → "+source.Context.TargetSchema+"."+source.Context.TargetTable)+". Distances from different spaces are not compared.",12));
        if(spaces.Length==0){vectorHealthBody.Children.Add(Text("This column holds one set of vectors. Comparing needs a second set: rows with a different model, revision or pipeline value.",14));status.Text="One set of vectors in this column · nothing to compare it with";return;}
        var target=new ComboBox{Header="Compare with",ItemsSource=spaces,SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var results=new StackPanel{Spacing=12};
        async Task Load(CancellationToken token)
        {
            target.IsEnabled=false;try{
            results.Children.Clear();var other=(VectorScope)target.SelectedItem;
            var candidates=await Connected().VectorCounterparts(source,other,anchor,token);
            if(candidates.Count==0){results.Children.Add(Text("No embedding for this record in the other space.",18));results.Children.Add(Text("These pipelines may cover different records. Choose another record, or explore the other space. No vector from another set was substituted.",13));results.Children.Add(Button("Explore other space",async(_,_)=>await OpenVectorInvestigation(source.Vector,new(source.Vector.Schema,source.Vector.Table,source.Vector.Column,other,Label:source.LabelKey))));return;}
            if(candidates.Count>1)
            {
                results.Children.Add(Text($"{candidates.Count:N0} matching embeddings · choose the sense or chunk to compare (up to 500)",14));
                var choices=new ComboBox{ItemsSource=candidates.Select((r,i)=>$"{i+1}. {CounterpartCaption(r)}").ToArray(),SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};results.Children.Add(choices);
                results.Children.Add(Button("Compare selected embedding",async(_,_)=>{if(operation!=null)return;await Work(t=>RenderPipelineComparison(source,scope,other,anchor,candidates[choices.SelectedIndex],results,t),vectorReadOnly:true);},true));return;
            }
            await RenderPipelineComparison(source,scope,other,anchor,candidates[0],results,token);
            }finally{target.IsEnabled=true;}
        }
        vectorHealthBody.Children.Add(Row(target,Button("Compare",async(_,_)=>{if(operation==null)await Work(Load,vectorReadOnly:true);},true)));vectorHealthBody.Children.Add(results);
        await Load(ct);status.Text="Pipeline comparison ready · read-only";
    },vectorReadOnly:true);

    async Task RenderPipelineComparison(VectorSource source,VectorScope leftScope,VectorScope rightScope,VectorItem leftAnchor,VectorItem rightAnchor,StackPanel results,CancellationToken ct)
    {
        var left=await Connected().VectorRows(source,leftScope,"",leftAnchor.Values,metric.SelectedIndex,ct);
        var right=await Connected().VectorRows(source,rightScope,"",rightAnchor.Values,metric.SelectedIndex,ct);
        var report=VectorPipelineReport.Create(source,leftAnchor,left,right);
        results.Children.Clear();results.Children.Add(Text($"{report.Shared.Count} shared records · {report.LeftOnly.Count} only on left · {report.RightOnly.Count} only on right",18));
        results.Children.Add(Text("Top 25 embedding rows per space, grouped by underlying record; the anchor record is excluded. This compares neighbor membership, not semantic quality. Reads are live and can reflect concurrent changes.",12));
        var grid=new Grid{ColumnSpacing=12};for(int i=0;i<3;i++)grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        void Column(int column,string title,string caption,IEnumerable<string> lines)
        {
            var body=Stack(Text(title,16),Text(caption,11));foreach(var line in lines)body.Children.Add(Text(line,13));
            var card=new Border{Padding=new Thickness(14),CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(22,111,194,200)),Child=body};Grid.SetColumn(card,column);grid.Children.Add(card);
        }
        Column(0,"Left only",leftScope.ToString(),report.LeftOnly.Select(r=>$"#{report.Left.IndexOf(r)+1}  {r.Label}"));
        Column(1,"Shared",$"{ExperimentLabel(leftAnchor)} ↔ {ExperimentLabel(rightAnchor)}",report.Shared.Select(r=>{int old=r.LeftRank,now=r.RightRank;return $"{r.Label}\n#{old} → #{now} · "+(old==now?"same rank":now<old?$"↑ {old-now}":$"↓ {now-old}");}));
        Column(2,"Right only",rightScope.ToString(),report.RightOnly.Select(r=>$"#{report.Right.IndexOf(r)+1}  {r.Label}"));results.Children.Add(grid);
    }
    async void SearchQualityClick(object sender,RoutedEventArgs e)=>await Work(async ct=>
    {
        var (source,scope,anchor)=VectorExperimentContext();
        int selectedMetric=metric.SelectedIndex;var report=await Connected().VectorSearchQuality(source,scope,anchor,selectedMetric,ct);
        ExperimentHeader("Search quality",ExperimentLabel(anchor)+" · "+scope);
        string metricName=selectedMetric==1?"L2":selectedMetric==2?"negative inner product":"cosine";
        vectorHealthBody.Children.Add(Text($"{metricName} · top 25 including anchor · same repeatable-read snapshot",12));
        vectorHealthBody.Children.Add(Text(report.Planned.Approximate?$"{report.Recall:P1} overlap with exact results":"Postgres chose an exact search plan",24));
        vectorHealthBody.Children.Add(Text(report.Planned.Approximate?$"{report.Shared} of {report.Denominator} exact neighbors returned · {report.Planned.Rows.Count} planned results":"No HNSW or IVFFlat index was used. This is an exact-plan comparison, not an ANN recall measurement.",14));
        var summary=new Grid{ColumnSpacing=10};for(int i=0;i<3;i++)summary.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        void Card(int column,string title,string value,string caption){var card=new Border{Padding=new Thickness(14),CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(22,111,194,200)),Child=Stack(Text(title,11),Text(value,26),Text(caption,11))};Grid.SetColumn(card,column);summary.Children.Add(card);}
        Card(0,"ROW OVERLAP",$"{report.Shared}/{report.Denominator}","Primary-key matches · top 25");Card(1,"PLANNED",$"{report.Planned.Milliseconds:0.00} ms",$"{report.Planned.Rows.Count} rows · "+(report.Planned.Approximate?"approximate index":"exact plan"));Card(2,"EXACT BASELINE",$"{report.Exact.Milliseconds:0.00} ms",$"{report.Exact.Rows.Count} rows · ANN disabled");vectorHealthBody.Children.Add(summary);
        vectorHealthBody.Children.Add(Text(report.Planned.Indexes.Length==0?"No index used":"Indexes: "+string.Join(", ",report.Planned.Indexes),12));
        vectorHealthBody.Children.Add(Text("One query example, not an overall benchmark. Server execution times exclude network and rendering; cache/order effects apply. Ties at the cutoff can change row overlap. Each SELECT is executed for its plan and result rows. No index was created or tuned.",12));
        var keys=report.Planned.Rows.Select(r=>VectorExperimentIdentity.Key(source,r,false)).ToHashSet();
        var missed=report.Exact.Rows.Where(r=>!keys.Contains(VectorExperimentIdentity.Key(source,r,false))).ToList();
        vectorHealthBody.Children.Add(Text(missed.Count==0?"No exact neighbors missed in this check":"Missed exact neighbors",18));foreach(var row in missed)vectorHealthBody.Children.Add(Text(ExperimentLabel(row)+$" · distance {row.Distance:0.000000}",13));
        vectorHealthBody.Children.Add(new Expander{Header="Query plan details",Content=Stack(Text("PLANNED\n"+report.Planned.Plan,11),Text("EXACT\n"+report.Exact.Plan,11)),HorizontalAlignment=HorizontalAlignment.Stretch});
        status.Text="Search check ready · read-only snapshot · no persistent settings changed";
    },vectorReadOnly:true);
}
