using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
namespace DbCowboy;

public sealed partial class MainWindow
{
    readonly ComboBox colorBy = new() { PlaceholderText = "Color by metadata", MinWidth = 180 };
    readonly ScrollViewer mapViewport = new() { ZoomMode = ZoomMode.Enabled, MinZoomFactor = .5f, MaxZoomFactor = 6f, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock vectorHealth = Text("No sample loaded", 12), vectorLegend = Text("", 11);
    readonly HashSet<int> selectedPoints = []; readonly Dictionary<int, Point> pointPositions = [];
    readonly Windows.UI.Color[] pointColors = [Windows.UI.Color.FromArgb(255,233,180,105),Windows.UI.Color.FromArgb(255,111,194,200),Windows.UI.Color.FromArgb(255,185,151,233),Windows.UI.Color.FromArgb(255,131,196,145),Windows.UI.Color.FromArgb(255,238,146,130),Windows.UI.Color.FromArgb(255,120,169,226)];
    Point? lassoStart; Rectangle? selectionRect; bool populatingColors;
    async void MapClick(object sender, RoutedEventArgs e) => await Work(LoadVectorInvestigation, vectorReadOnly: true);
    string PointGroup(PointData point)
    {
        if(colorBy.SelectedItem is not string property||property=="Single color")return "Embeddings";
        return pointMetadata.GetValueOrDefault(point)?.GetValueOrDefault(property,"NULL")??"NULL";
    }
    /// <summary>The point palette is tuned for the dark stage; on the light stage each colour is deepened so points stay visible.</summary>
    Windows.UI.Color MapInk(Windows.UI.Color color) => HighContrast || map.ActualTheme == ElementTheme.Dark
        ? color
        : Windows.UI.Color.FromArgb(color.A, (byte)(color.R * .62), (byte)(color.G * .62), (byte)(color.B * .62));
    void DrawMap()
    {
        map.Children.Clear();pointPositions.Clear();if(projection==null||points.Count==0){DrawVectorEmpty();return;} if(vectorMode.SelectedIndex==0){DrawConstellation();return;} if(vectorMode.SelectedIndex==2){DrawSimilarityMatrix();return;} var projected=projection.Points;
        double minX=projected.Min(p=>p[0]),maxX=projected.Max(p=>p[0]),minY=projected.Min(p=>p[1]),maxY=projected.Max(p=>p[1]);double width=Math.Max(100,map.ActualWidth-80),height=Math.Max(100,map.ActualHeight-80);
        double rangeX=maxX-minX,rangeY=maxY-minY;double scale=rangeX<1e-12&&rangeY<1e-12?1:Math.Min(rangeX<1e-12?double.PositiveInfinity:width/rangeX,rangeY<1e-12?double.PositiveInfinity:height/rangeY);var groups=points.Select(PointGroup).Distinct().OrderBy(x=>x,StringComparer.Ordinal).ToList();var palette=groups.Select((g,i)=>(g,color:pointColors[i%pointColors.Length])).ToDictionary(x=>x.g,x=>x.color);
        vectorLegend.Text=groups.Count<=6?string.Join("   ·   ",groups.Select(g=>$"{g} ({points.Count(p=>PointGroup(p)==g)})")):$"{groups.Count} categories · colors repeat after {pointColors.Length} groups; inspect points for exact values";
        for(int i=0;i<points.Count;i++)
        {
            int index=i;var sample=points[i];double x=map.ActualWidth/2+(projected[i][0]-(minX+maxX)/2)*scale,y=map.ActualHeight/2-(projected[i][1]-(minY+maxY)/2)*scale;pointPositions[i]=new(x,y);
            var dot=new Ellipse{Width=selectedPoints.Contains(i)?14:9,Height=selectedPoints.Contains(i)?14:9,Fill=new SolidColorBrush(MapInk(palette[PointGroup(sample)])),Opacity=selectedPoints.Count==0||selectedPoints.Contains(i)?1:.45,Stroke=selectedPoints.Contains(i)?new SolidColorBrush(Microsoft.UI.Colors.White):null,StrokeThickness=2};
            Canvas.SetLeft(dot,x-dot.Width/2);Canvas.SetTop(dot,y-dot.Height/2);ToolTipService.SetToolTip(dot,vectorItems.Count>i?vectorItems[i].Label:"Embedding");
            dot.PointerPressed+=(_,args)=>{if(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))return;FocusVector(index);args.Handled=true;};map.Children.Add(dot);
        }
    }
    void InspectPoint(PointData point)
    {
        UpdateVectorLens(point); using var doc=JsonDocument.Parse(point.Metadata);var strongest=point.Values.Select((value,index)=>(value,index)).OrderByDescending(x=>Math.Abs(x.value)).Take(12);
        vectorDetails.Text=$"{point.Values.Length} dimensions\r\nNorm {Embeddings.Norm(point.Values):N5}\r\n\r\n{EmbeddingMetadata.Pretty(point.Metadata)}\r\n\r\nLargest components\r\n"+string.Join("\r\n",strongest.Select(x=>$"[{x.index}]  {x.value.ToString("0.00000",CultureInfo.InvariantCulture)}"))+"\r\n\r\nVector copied into the search field. Nearest-neighbor distances are computed by PostgreSQL.";
    }
    void MapPointerPressed(object sender,PointerRoutedEventArgs e)
    {
        if(vectorMode.SelectedIndex!=1)return;
        if(!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))return;
        lassoStart=e.GetCurrentPoint(map).Position;selectionRect=new Rectangle{Stroke=new SolidColorBrush(pointColors[0]),StrokeThickness=1,Fill=new SolidColorBrush(Windows.UI.Color.FromArgb(35,233,180,105))};map.Children.Add(selectionRect);map.CapturePointer(e.Pointer);e.Handled=true;
    }
    void MapPointerMoved(object sender,PointerRoutedEventArgs e)
    {
        if(lassoStart is not Point start||selectionRect==null)return;var p=e.GetCurrentPoint(map).Position;Canvas.SetLeft(selectionRect,Math.Min(start.X,p.X));Canvas.SetTop(selectionRect,Math.Min(start.Y,p.Y));selectionRect.Width=Math.Abs(start.X-p.X);selectionRect.Height=Math.Abs(start.Y-p.Y);
    }
    void MapPointerReleased(object sender,PointerRoutedEventArgs e)
    {
        if(lassoStart is not Point start)return;var end=e.GetCurrentPoint(map).Position;selectedPoints.Clear();foreach(var (i,p) in pointPositions)if(p.X>=Math.Min(start.X,end.X)&&p.X<=Math.Max(start.X,end.X)&&p.Y>=Math.Min(start.Y,end.Y)&&p.Y<=Math.Max(start.Y,end.Y))selectedPoints.Add(i);lassoStart=null;selectionRect=null;map.ReleasePointerCapture(e.Pointer);DrawMap();
        if(selectedPoints.Count==1){var sample=points[selectedPoints.First()];vectorInput.Text=Embeddings.Serialize(sample.Values);InspectPoint(sample);}else vectorDetails.Text=$"{selectedPoints.Count} selected embeddings\r\n\r\n"+string.Join("\r\n\r\n",selectedPoints.Take(15).Select(i=>points[i].Metadata))+"\r\n\r\nSelect a single point to seed nearest-neighbor search.";
    }
    async void NeighborsClick(object sender,RoutedEventArgs e)=>await Work(async ct=>
    {
        if(vectorSource == null || vectorSource.Vector != vectorColumn.SelectedItem as VectorColumn) throw new InvalidOperationException("Explore a vector column first.");
        var scope=vectorModel.SelectedItem as VectorScope ?? throw new InvalidOperationException("Explore the column first.");
        var embedding=Embeddings.Parse(vectorInput.Text);
        if(points.Count>0 && embedding.Length!=points[0].Values.Length) throw new InvalidOperationException("The pasted vector must have the same number of dimensions as the loaded column.");
        if(metric.SelectedIndex==0&&embedding.All(x=>x==0))throw new InvalidOperationException("Cosine distance is undefined for a zero vector.");
        string anchor=focusedVector<vectorItems.Count&&Embeddings.Serialize(vectorItems[focusedVector].Values)==vectorInput.Text?vectorItems[focusedVector].Label:"custom vector";
        var anchorBookmark=anchor!="custom vector"&&focusedVector<vectorItems.Count?VectorInvestigationState.Bookmark(vectorSource,vectorItems[focusedVector]):null;
        var source=vectorSource;var rows=await Connected().VectorRows(source,scope,"",embedding,metric.SelectedIndex,ct);
        if(vectorSource!=source || vectorColumn.SelectedItem as VectorColumn!=source.Vector || vectorModel.SelectedItem as VectorScope!=scope)throw new OperationCanceledException();
        await PresentVectors(rows,ct,$"Exact PostgreSQL top 25 for {anchor} · {metric.SelectedItem} · ascending distance");
        vectorAnchor=anchorBookmark;ShowVectorSurface("explore");SaveWorkspaceSoon();status.Text="Read-only PostgreSQL neighbors loaded in Vector Lab";
    }, vectorReadOnly: true);
}
