using System.Data;
using System.Globalization;
using System.Text.Json;

namespace DbCowboy;

public sealed record ColumnInfo(string Name, string Type, bool Nullable, bool Generated, string? Default);
public sealed record ForeignKeyInfo(string Name, string SourceSchema, string SourceTable, string[] SourceColumns, string TargetSchema, string TargetTable, string[] TargetColumns);
public enum ChangeKind { Insert, Update, Delete }
public sealed record PendingChange(ChangeKind Kind, Relation Relation, Dictionary<string, string?> Values, Dictionary<string, string?> Original)
{
    public string Description => $"{Kind.ToString().ToUpperInvariant()} {Relation.Schema}.{Relation.Name} · " + (Kind == ChangeKind.Delete ? "delete selected row" : string.Join(", ", Values.Select(x => x.Key + " = " + (x.Value == null ? "NULL" : x.Value.Length > 50 ? x.Value[..50] + "…" : x.Value))));
    public static Dictionary<string, string?> Snapshot(DataRow row) => row.Table.Columns.Cast<DataColumn>().ToDictionary(c => c.ColumnName, c => row[c] == DBNull.Value ? null : Convert.ToString(row[c], CultureInfo.InvariantCulture));
}
public sealed record PlanNode(string Label, string? Relation, double Cost, long Rows, List<PlanNode> Children, string Detail);
public static class QueryPlans
{
    public static PlanNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement[0].GetProperty("Plan");
        PlanNode Read(JsonElement node)
        {
            string label = node.GetProperty("Node Type").GetString()!;
            string? relation = node.TryGetProperty("Relation Name", out var r) ? r.GetString() : null;
            double cost = node.TryGetProperty("Total Cost", out var c) ? c.GetDouble() : 0;
            long rows = node.TryGetProperty("Plan Rows", out var n) ? n.GetInt64() : 0;
            var children = node.TryGetProperty("Plans", out var plans) ? plans.EnumerateArray().Select(Read).ToList() : [];
            return new(label, relation, cost, rows, children, JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return Read(root);
    }
}
