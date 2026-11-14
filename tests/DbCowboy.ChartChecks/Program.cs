using DbCowboy;
using System.Data;
using System.Text.Json;
using System.Xml.Linq;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; }
DataTable Table(params object[][] rows)
{
    var table = new DataTable(); foreach (var name in new[] { "day", "sales", "cost", "team" }) table.Columns.Add(name);
    foreach (var row in rows) table.Rows.Add(row); return table;
}
var t = Table(["2026-10-01", "2", "1", "A"], ["2026-10-03", "8", "3", "B"], ["2026-10-10", DBNull.Value, "4", "A"], ["2026-10-03", "4", "2", "A"]);
var d = new ChartDefinition(Guid.NewGuid(), X: "day", Values: ["sales", "cost"]);
var data = ChartModel.Build(t, d);
Check(data.Valid && data.Axis == "Time" && data.Series.Count == 2, "multiple measures with inferred time axis");
Check(data.Series[0].Points[1].Y == 12, "duplicate dates aggregate");
Check(data.Series[0].Points[2].Y == null, "all NULL group remains a gap");
Check(data.Series[0].Points[2].X - data.Series[0].Points[1].X == 7 * 86400000d, "elapsed time controls spacing");
var split = ChartModel.Build(t, d with { Split = "team" });
Check(split.Series.Count == 4 && split.Series.Single(s => s.Name == "sales · B").Points[0].Y == null, "split series align absent categories as gaps");
Check(ChartModel.Build(t, d with { Aggregate = "Average" }).Series[0].Points[1].Y == 6, "average ignores NULL");
Check(ChartModel.Build(t, d with { Aggregate = "Count" }).Series[0].Points[2].Y == 1, "Count counts rows including NULL measures");
Check(ChartModel.Build(t, d with { X = "renamed" }).Message.Contains("Missing columns"), "schema changes report mapping error");
Check(!ChartModel.Build(t, d with { Kind = "Scatter", Aggregate = "Raw" }).Valid, "scatter rejects time axis");
var numeric = Table(["1", "2", "1", "A"], ["10", "3", "2", "A"], ["2", "0", "3", "A"]);
var scatter = d with { Kind = "Scatter", Values = ["sales"], Aggregate = "Raw" };
var numbers = ChartModel.Build(numeric, scatter);
Check(ChartModel.Suggest(numeric, 1) is { ResultSet: 1, Values: ["sales"] }, "one-click mapping prefers a measure distinct from X");
Check(numbers.Valid && numbers.Axis == "Numeric" && numbers.Series[0].Points.Select(p => p.X).SequenceEqual(new[] { 1d, 2, 10 }), "scatter preserves and sorts numeric observations");
Check(!ChartModel.Build(numeric, scatter with { Aggregate = "Sum" }).Valid, "scatter rejects aggregation");
foreach (var kind in ChartModel.Kinds)
{
    var definition = d with { Kind = kind, Title = "<Sales & costs>", Values = kind == "Donut" ? ["sales"] : d.Values };
    var source = t;
    if (kind == "Scatter") { definition = scatter; source = numeric; }
    var dataset = ChartModel.Build(source, definition);
    Check(dataset.Valid, kind + " accepts valid data");
    var scene = ChartLayout.Build(definition, dataset, 1000, 500);
    var xml = XDocument.Parse(scene.Svg());
    Check(xml.Root?.Name.LocalName == "svg" && !scene.Svg().Contains("NaN") && !scene.Svg().Contains("Infinity"), kind + " produces finite valid SVG");
    Directory.CreateDirectory("artifacts/chart-qa/exports"); File.WriteAllText("artifacts/chart-qa/exports/" + kind.Replace(' ', '-') + ".svg", scene.Svg());
}
var negative = Table(["x", "-2", "1", "A"]);
Check(!ChartModel.Build(negative, d with { Kind = "Donut", Values = ["sales"] }).Valid, "donut rejects negative values");
Check(ChartModel.Build(negative, d with { Kind = "Bar" }).Valid, "bars accept negatives");
var bad = Table(["x", "NaN", "1", "A"], ["y", "3", "2", "A"]);
var skipped = ChartModel.Build(bad, d with { Values = ["sales"] });
Check(skipped.Valid && skipped.Skipped == 1 && skipped.Series[0].Points[0].Y == null, "nonfinite measure skipped without becoming zero");
var zero = Table(["x", "0", "1", "A"]);
Check(ChartModel.Build(zero, d with { Values = ["sales"] }).Series[0].Points[0].Y == 0, "zero remains a real value");
Check(!ChartModel.Build(zero, d with { Kind = "Donut", Values = ["sales"] }).Valid, "zero-total donut rejected");
foreach (var extreme in new[] { "1e308", "5e-324" })
{
    var extremeData = Table([extreme, extreme, "1", "A"]);
    var extremeDefinition = scatter;
    var extremeScene = ChartLayout.Build(extremeDefinition, ChartModel.Build(extremeData, extremeDefinition), 1000, 500);
    Check(extremeScene.Marks.All(m => double.IsFinite(m.X) && double.IsFinite(m.Y)) && !extremeScene.Svg().Contains("NaN"), "finite constant axis for " + extreme);
}
var filtered = t.Clone(); filtered.ImportRow(t.Rows[1]); filtered.ImportRow(t.Rows[3]);
Check(ChartModel.Build(filtered, d).Rows == 2 && ChartModel.Build(filtered, d).Series[0].Points.Single().Y == 12, "filtered source recalculates chart totals");
var tooManySeries = Table(Enumerable.Range(0, 25).Select(i => new object[] { "x", "1", "2", "team" + i }).ToArray());
Check(!ChartModel.Build(tooManySeries, d with { Split = "team" }).Valid, "series explosion reports error instead of silent truncation");
var opposite = Table(["-1e308", "-1e308", "1", "A"], ["1e308", "1e308", "1", "A"]);
var oppositeScene = ChartLayout.Build(scatter, ChartModel.Build(opposite, scatter), 1000, 500);
Check(oppositeScene.Marks.All(m => double.IsFinite(m.X) && double.IsFinite(m.Y)) && !oppositeScene.Svg().Contains("Infinity"), "opposing large coordinates avoid scale overflow");
var empty = t.Clone(); Check(!ChartModel.Build(empty, d).Valid, "empty result explicit");
var restored = JsonSerializer.Deserialize<QueryDocument>(JsonSerializer.Serialize(new QueryDocument(Guid.NewGuid(), "Sales", "SELECT 1", Charts: [d with { ResultSet = 1, Split = "team", Unit = "USD", Palette = "Ocean", Legend = false }])) )!;
Check(restored.Charts is [{ ResultSet: 1, Split: "team", Unit: "USD", Palette: "Ocean", Legend: false }] && restored.Charts[0].Values!.SequenceEqual(d.Values!), "query JSON round trip preserves chart mappings and style");
Check(JsonSerializer.Deserialize<QueryDocument>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Title\":\"Old\",\"Sql\":\"select 1\"}")!.Charts == null, "older workspace remains compatible");
var sql = "SELECT day::date AS day, sales, cost, team FROM (VALUES ('2026-10-01',2,1,'A'),('2026-10-03',8,3,'B'),('2026-10-10',NULL,4,'A'),('2026-10-03',4,2,'A')) AS v(day,sales,cost,team) ORDER BY day; SELECT 1 AS x, 5 AS y";
if (!args.Contains("--no-db"))
{
await using var db = new Db(new Profile { Host = "127.0.0.1", Port = 55439, Database = "cowboy_test", Username = "postgres", Ssl = "Disable" }, "");
var result = await db.Query(sql, CancellationToken.None, true);
Check(result.Results.Count == 2 && ChartModel.Build(result.Results[0], d).Series[0].Points[1].Y == 12, "production query reader to chart including multiple result sets");
var cap = await db.Query("SELECT i AS x, i AS sales FROM generate_series(1,2100) i", CancellationToken.None, true);
Check(cap.Truncated && cap.Table.Rows.Count == 2000 && ChartModel.Build(cap.Table, d with { X = "x", Values = ["sales"] }).Rows == 2000, "query cap retained through chart model");
}
var lightScene=ChartLayout.Build(d,ChartModel.Build(t,d),1000,500).LightSurface();
Check(lightScene.Background=="#FFFFFF"&&lightScene.Svg().Contains("fill=\"#FFFFFF\"")&&!lightScene.Marks.Any(m=>m.Color.Equals("#DCE5EF",StringComparison.OrdinalIgnoreCase)),"light chart native/export scene retains readable ink and matching background");
Console.WriteLine($"{checks} chart checks passed.");
