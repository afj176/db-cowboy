namespace DbCowboy;

public sealed record QueryDocument(Guid Id, string Title, string Sql, bool Favorite = false, List<ChartDefinition>? Charts = null);
public sealed record ConnectionWorkspace(List<QueryDocument> Documents, Guid ActiveDocument, string? Schema = null, string? Table = null, string View = "Query");
// Earlier versions also stored an editor "FontSize". It is no longer a setting; an old value is ignored when read.
public sealed record AppPreferences(string Theme = "Default", bool ReducedMotion = false);
/// <summary>Where the window was last: restored bounds in physical pixels, and whether it was maximized.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);
public sealed record SavedSnippet(Guid Id, string Name, string Sql);
public sealed record VectorBookmark(Dictionary<string,string> Keys);
public sealed record VectorInvestigation(string Schema, string Table, string Column, VectorScope? Scope = null, string Search = "", int Mode = 0, int Metric = 0, string Color = "Single color", string? Label = null, VectorBookmark? Focus = null, VectorBookmark? Pin = null, VectorBookmark? Anchor = null, string Name = "Investigation");
// A hand-edited or partly written file can hold null for a section; each one falls back to its empty default.
public sealed class WorkspaceState
{
    public Dictionary<Guid, ConnectionWorkspace> Connections { get; set => field = value ?? []; } = [];
    public ConnectionWorkspace Scratch { get; set => field = value ?? new([], Guid.Empty); } = new([], Guid.Empty);
    public List<SavedSnippet> Snippets { get; set => field = value ?? []; } = [];
    public List<QueryDocument> ClosedDocuments { get; set => field = value ?? []; } = [];
    public AppPreferences Preferences { get; set => field = value ?? new(); } = new();
    public Dictionary<Guid,VectorInvestigation> VectorLast { get; set => field = value ?? []; } = [];
    public Dictionary<Guid,List<VectorInvestigation>> VectorSaved { get; set => field = value ?? []; } = [];
    public Guid? LastSelectedConnection { get; set; }
    public WindowPlacement? Window { get; set; }
    public const string File = "workspace.json";
    public void Save() => Store.Write(File, this);
}
