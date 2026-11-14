using System.Data;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

/// <summary>
/// Live views for installed extensions. Every query is read-only, bounded with LIMIT, and reads the
/// extension's own catalog or statistics views; nothing here writes or changes settings.
/// </summary>
public sealed partial class MainWindow
{
    static IEnumerable<DataRow> Rows(QueryResult result) => result.Table.Rows.Cast<DataRow>();
    static string Cell(DataRow row, int column) => row[column] == DBNull.Value ? "" : (string)row[column];
    static double Number(DataRow row, int column) => double.TryParse(Cell(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    static string Whole(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
    static string Plural(double count, string one, string many) => Whole(count) + " " + (count == 1 ? one : many);
    static string Bytes(double bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"]; int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }
    static string Duration(double milliseconds) => milliseconds >= 60000 ? (milliseconds / 60000).ToString("0.#") + " min" : milliseconds >= 1000 ? (milliseconds / 1000).ToString("0.##") + " s" : milliseconds.ToString("0.##") + " ms";
    /// <summary>Schema prefix for an extension's functions and views, which may live outside the search path.</summary>
    static string In(ExtensionState state) => Db.Q(state.Schema ?? "public") + ".";

    Task<FrameworkElement> ExtensionInsight(ExtensionState state, Db active, CancellationToken ct) => state.Guide!.Insight switch
    {
        "vector" => VectorInsight(active, ct),
        "statements" => StatementsInsight(state, active, ct),
        "postgis" => PostgisInsight(state, active, ct),
        "timescaledb" => TimescaleInsight(active, ct),
        "cron" => CronInsight(active, ct),
        "trgm" => Task.FromResult(TrigramInsight(state, active)),
        "buffercache" => BufferCacheInsight(state, active, ct),
        "fdw" => ForeignDataInsight(state, active, ct),
        // Child partitions are counted from pg_inherits; "retention" is NULL when pg_partman never drops old partitions.
        "partman" => ListingInsight(active, ct, "Partition sets", "Parent tables whose partitions pg_partman creates and retires. Partitions counts the child tables that exist now, including the default partition.",
            $"SELECT parent_table, control, partition_interval::text, premake::text, coalesce(retention, 'keep all'), (SELECT count(*) FROM pg_inherits i WHERE i.inhparent = to_regclass(parent_table))::text FROM {In(state)}part_config ORDER BY 1 LIMIT 100", ["Parent table", "Control column", "Interval", "Premake", "Retention", "Partitions"], "No partition sets are configured yet."),
        "pgmq" => QueueInsight(active, ct),
        "citus" => CitusInsight(active, ct),
        // Every graph owns the internal labels _ag_label_vertex and _ag_label_edge; they are not labels anyone defined.
        "age" => ListingInsight(active, ct, "Graphs", "Each graph with the vertex and edge labels defined in it.",
            """
            SELECT g.name::text,
                   (SELECT count(*) || coalesce(' · ' || string_agg(l.name::text, ', ' ORDER BY l.name), '') FROM ag_catalog.ag_label l WHERE l.graph = g.graphid AND l.kind = 'v' AND l.name::text <> '_ag_label_vertex'),
                   (SELECT count(*) || coalesce(' · ' || string_agg(l.name::text, ', ' ORDER BY l.name), '') FROM ag_catalog.ag_label l WHERE l.graph = g.graphid AND l.kind = 'e' AND l.name::text <> '_ag_label_edge')
            FROM ag_catalog.ag_graph g ORDER BY 1 LIMIT 100
            """, ["Graph", "Vertex labels", "Edge labels"], "No graphs have been created yet."),
        _ => Task.FromResult<FrameworkElement>(new StackPanel())
    };

    /// <summary>A catalog listing shared by extensions whose state is best read as a short table.</summary>
    async Task<FrameworkElement> ListingInsight(Db active, CancellationToken ct, string title, string caption, string sql, string[] headers, string empty)
    {
        var result = await active.Query(sql, ct, true);
        var rows = Rows(result).Select(r => headers.Select((_, i) => Cell(r, i)).ToArray()).ToList();
        return Section(title, rows.Count == 0 ? Muted(empty, 13) : MiniTable(headers, rows), caption);
    }

    // ── pgvector ────────────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> VectorInsight(Db active, CancellationToken ct)
    {
        var columns = Rows(await active.Query(
            """
            SELECT n.nspname, c.relname, a.attname, t.typname, a.atttypmod, greatest(c.reltuples, 0)::bigint,
                   coalesce((SELECT string_agg(i.relname || ' · ' || am.amname, ', ')
                             FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_am am ON am.oid = i.relam
                             WHERE x.indrelid = c.oid AND a.attnum = ANY (x.indkey::int2[]) AND am.amname IN ('hnsw', 'ivfflat', 'diskann')), '')
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            WHERE t.typname IN ('vector', 'halfvec', 'sparsevec') AND a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'm')
              AND has_table_privilege(c.oid, 'SELECT')
            ORDER BY 1, 2, 3 LIMIT 200
            """, ct, true)).ToList();
        var indexes = Rows(await active.Query(
            """
            SELECT n.nspname || '.' || t.relname, i.relname, am.amname, pg_relation_size(i.oid),
                   coalesce(array_to_string(i.reloptions, ', '), 'default parameters'), x.indisvalid,
                   (SELECT string_agg(oc.opcname, ', ') FROM pg_opclass oc WHERE oc.oid = ANY (x.indclass::oid[])),
                   coalesce(s.idx_scan, 0)
            FROM pg_index x
            JOIN pg_class i ON i.oid = x.indexrelid
            JOIN pg_class t ON t.oid = x.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am am ON am.oid = i.relam
            LEFT JOIN pg_stat_user_indexes s ON s.indexrelid = i.oid
            WHERE am.amname IN ('hnsw', 'ivfflat', 'diskann')
            ORDER BY 4 DESC LIMIT 100
            """, ct, true)).ToList();
        var tuning = Rows(await active.Query("SELECT coalesce(current_setting('hnsw.ef_search', true), ''), coalesce(current_setting('ivfflat.probes', true), '')", ct, true)).First();

        int unindexed = columns.Count(c => Cell(c, 6).Length == 0);
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Flow(150,
            Tile("Vector columns", columns.Count.ToString(), out _, columns.Count == 200 ? "first 200 shown" : ""),
            Tile("ANN indexes", indexes.Count.ToString(), out _, indexes.Count == 0 ? "searches scan every row" : Bytes(indexes.Sum(i => Number(i, 3))) + " on disk"),
            Tile("Without an index", unindexed.ToString(), out _, unindexed == 0 ? "" : "exact scan only"),
            Tile("Recall tuning", Cell(tuning, 0).Length > 0 ? "ef " + Cell(tuning, 0) : "–", out _, Cell(tuning, 1).Length > 0 ? "ivfflat.probes " + Cell(tuning, 1) : "hnsw.ef_search")));

        if (columns.Count == 0)
        {
            panel.Children.Add(EmptyState("01-got-you", "pgvector is ready", "No table has a vector column yet. Add one, then come back to index and explore it.",
                Button("Draft a vector table", (_, _) => OpenSqlDraft("Vector table", "-- A minimal table with a vector column. Match the dimension to the vectors you will store.\rCREATE TABLE documents (\r    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,\r    content text NOT NULL,\r    embedding vector(384)\r);"), true)));
            return Section("Vector columns and indexes", panel);
        }

        var list = new StackPanel { Spacing = 8 };
        foreach (var column in columns.Take(40))
        {
            string schema = Cell(column, 0), table = Cell(column, 1), name = Cell(column, 2), type = Cell(column, 3), indexed = Cell(column, 6);
            int dimensions = (int)Number(column, 4);
            var heading = Text($"{schema}.{table} · {name}", 14); heading.FontSize = 14; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; heading.TextWrapping = TextWrapping.NoWrap; heading.TextTrimming = TextTrimming.CharacterEllipsis;
            var facts = Row(Pill(dimensions > 0 ? $"{type}({dimensions})" : type + " · any dimension"), Pill("~" + Whole(Number(column, 5)) + " rows"));
            facts.Children.Add(indexed.Length > 0 ? Pill(indexed, Tone.Good) : Pill("No ANN index", Tone.Warn));
            var actions = Row();
            if (indexed.Length == 0) actions.Children.Add(Button("Draft index", (_, _) => OpenSqlDraft("Index " + table + "." + name, VectorIndexDraft(schema, table, name, type, dimensions))));
            var explore = Button("Explore", (_, _) =>
            {
                Navigate("Vector lab");
                if ((vectorColumn.ItemsSource as IEnumerable<VectorColumn>)?.FirstOrDefault(v => v.Schema == schema && v.Table == table && v.Column == name) is { } match) vectorColumn.SelectedItem = match;
            });
            ToolTipService.SetToolTip(explore, "Open this column in Vector lab");
            if (type != "sparsevec") actions.Children.Add(explore);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var left = new StackPanel { Spacing = 6 }; left.Children.Add(heading); left.Children.Add(ScrollTools(facts));
            row.Children.Add(left); Grid.SetColumn(actions, 1); actions.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(actions);
            list.Children.Add(Surface(row, 14, 8));
        }
        if (columns.Count > 40) list.Children.Add(Muted($"{columns.Count - 40} more columns are not listed here.", 12));
        panel.Children.Add(list);

        if (indexes.Count > 0)
        {
            var bars = Bars(indexes.Take(15).Select(i => Bar(
                $"{Cell(i, 1)}  ·  {Cell(i, 0)}", Number(i, 3), Bytes(Number(i, 3)),
                $"{Cell(i, 2)} · {Cell(i, 6)} · {Cell(i, 4)} · {Plural(Number(i, 7), "scan", "scans")}" + (Cell(i, 5) == "t" ? "" : " · INVALID, rebuild required"))).ToList(), Tone.Good);
            panel.Children.Add(Stack(Eyebrow("Index size on disk"), bars));
        }
        return Section("Vector columns and indexes", panel, "An approximate (ANN) index trades a little recall for much faster nearest-neighbour search. Columns without one are still searchable, by scanning every row.");
    }

    static string VectorIndexDraft(string schema, string table, string column, string type, int dimensions)
    {
        string qualified = Db.Q(schema) + "." + Db.Q(table), family = type == "vector" ? "vector" : type;
        string index = Db.Q((table + "_" + column + "_hnsw_idx").ToLowerInvariant());
        int limit = type == "halfvec" ? 4000 : 2000;
        string wide = type != "sparsevec" && dimensions > limit
            ? $"-- NOTE: this column has {dimensions} dimensions. HNSW indexes {type} up to {limit}. Index a halfvec cast or a\r-- reduced-dimension expression instead, and query with the same expression.\r"
            : "";
        return
            $"-- Approximate nearest-neighbour index for {schema}.{table}.{column} ({type}{(dimensions > 0 ? $"({dimensions})" : "")}).\r" +
            $"-- Choose the operator class that matches the distance your queries use:\r" +
            $"--   {family}_cosine_ops  for  <=>   (cosine distance)\r" +
            $"--   {family}_l2_ops      for  <->   (Euclidean distance)\r" +
            $"--   {family}_ip_ops      for  <#>   (negative inner product)\r" +
            wide +
            "-- This build blocks writes to the table until it finishes. For a large, busy table run the same\r" +
            "-- statement with CREATE INDEX CONCURRENTLY from psql; DB Cowboy runs statements inside a transaction,\r" +
            "-- where CONCURRENTLY is not allowed.\r" +
            $"CREATE INDEX IF NOT EXISTS {index}\r    ON {qualified} USING hnsw ({Db.Q(column)} {family}_cosine_ops)\r    WITH (m = 16, ef_construction = 64);";
    }

    // ── pg_stat_statements ──────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> StatementsInsight(ExtensionState state, Db active, CancellationToken ct)
    {
        string view = In(state) + "pg_stat_statements";
        // The view covers every database on the server; this page is about the connected one.
        const string here = "dbid = (SELECT oid FROM pg_database WHERE datname = current_database())";
        async Task<List<DataRow>> Top(string total, string mean) => Rows(await active.Query(
            $"""
            SELECT regexp_replace(query, '\s+', ' ', 'g'), calls, {total}, {mean}, rows,
                   round(100.0 * shared_blks_hit / nullif(shared_blks_hit + shared_blks_read, 0), 1)
            FROM {view} WHERE {here} ORDER BY {total} DESC LIMIT 15
            """, ct, true)).ToList();
        List<DataRow> top; string totalColumn = "total_exec_time";
        try { top = await Top("total_exec_time", "mean_exec_time"); }
        // PostgreSQL 12 and earlier name these columns without "exec".
        catch (Npgsql.PostgresException error) when (error.SqlState == "42703") { totalColumn = "total_time"; top = await Top("total_time", "mean_time"); }
        var totals = Rows(await active.Query($"SELECT count(*), coalesce(sum({totalColumn}), 0), coalesce(sum(calls), 0) FROM {view} WHERE {here}", ct, true)).First();

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Flow(150,
            Tile("Statements tracked", Whole(Number(totals, 0)), out _),
            Tile("Total execution", Duration(Number(totals, 1)), out _, "in this database since statistics were last reset"),
            Tile("Calls", Whole(Number(totals, 2)), out _)));
        if (top.Count == 0) { panel.Children.Add(Muted("No statements have been recorded in this database yet.", 13)); return Section("Where the time goes", panel); }
        double all = Math.Max(Number(totals, 1), 1e-9);
        var bars = Bars(top.Select(row =>
        {
            string sql = Cell(row, 0), hit = Cell(row, 5);
            return Bar(sql.Length > 110 ? sql[..110] + "…" : sql, Number(row, 2), Duration(Number(row, 2)),
                $"{Number(row, 2) / all:P0} of this database's time · {Plural(Number(row, 1), "call", "calls")} · {Duration(Number(row, 3))} mean · {Plural(Number(row, 4), "row", "rows")}" + (hit.Length > 0 ? $" · {hit}% cache hits" : ""),
                () => OpenSqlDraft("Tracked statement", "-- From pg_stat_statements. Constants were replaced with $1, $2… placeholders;\r-- substitute real values before running or explaining it.\r" + sql));
        }).ToList());
        panel.Children.Add(bars);
        return Section("Where the time goes", panel, "The fifteen statements run in this database with the highest total execution time. Select one to open it in SQL.");
    }

    // ── PostGIS ─────────────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> PostgisInsight(ExtensionState state, Db active, CancellationToken ct)
    {
        string schema = In(state);
        var layers = Rows(await active.Query(
            $"""
            SELECT f_table_schema::text, f_table_name::text, f_geometry_column::text, type::text, srid::text, 'geometry' FROM {schema}geometry_columns
            UNION ALL
            SELECT f_table_schema::text, f_table_name::text, f_geography_column::text, type::text, srid::text, 'geography' FROM {schema}geography_columns
            ORDER BY 1, 2, 3 LIMIT 200
            """, ct, true)).ToList();
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Flow(150,
            Tile("Spatial columns", layers.Count.ToString(), out _),
            Tile("Reference systems", layers.Select(l => Cell(l, 4)).Distinct().Count().ToString(), out _, "distinct SRIDs in use")));
        if (layers.Count == 0) { panel.Children.Add(Muted("No geometry or geography columns exist yet.", 13)); return Section("Spatial columns", panel); }

        // Plot: representative points of the first rows, in the column's own coordinate system.
        var choose = new ComboBox { ItemsSource = layers.Select(l => $"{Cell(l, 0)}.{Cell(l, 1)} · {Cell(l, 2)}").ToArray(), SelectedIndex = 0, MinWidth = 280 };
        AutomationProperties.SetName(choose, "Spatial column to plot");
        var plot = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var caption = Muted("Plots up to 1,000 rows as points. Lines and polygons are shown by their centroid.", 12);
        var draw = Button("Plot sample", async (_, _) =>
        {
            var layer = layers[choose.SelectedIndex];
            caption.Text = "Reading…";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var sample = await active.Query(
                    $"SELECT {schema}ST_X(p), {schema}ST_Y(p) FROM (SELECT {schema}ST_Centroid({Db.Q(Cell(layer, 2))}::{schema}geometry) AS p FROM {Db.Q(Cell(layer, 0))}.{Db.Q(Cell(layer, 1))} WHERE {Db.Q(Cell(layer, 2))} IS NOT NULL LIMIT 1000) s WHERE NOT {schema}ST_IsEmpty(p)",
                    timeout.Token, true);
                var points = Rows(sample).Where(r => Cell(r, 0).Length > 0 && Cell(r, 1).Length > 0).Select(r => (Number(r, 0), Number(r, 1))).ToList();
                if (points.Count == 0) { plot.Content = null; caption.Text = "This column has no non-empty geometries to plot."; return; }
                plot.Content = PointMap(points);
                caption.Text = $"{points.Count:N0} points · first rows in storage order, not a random sample · X {points.Min(p => p.Item1):0.###} to {points.Max(p => p.Item1):0.###}, Y {points.Min(p => p.Item2):0.###} to {points.Max(p => p.Item2):0.###} · SRID {Cell(layer, 4)} units";
            }
            catch (Exception error) { plot.Content = null; caption.Text = "The sample could not be read: " + Describe(error); }
        }, true);
        panel.Children.Add(Stack(Eyebrow("Plot a column"), ScrollTools(Row(choose, draw)), plot, caption));
        panel.Children.Add(MiniTable(["Table", "Column", "Type", "SRID", "Kind"], layers.Take(30).Select(l => new[] { Cell(l, 0) + "." + Cell(l, 1), Cell(l, 2), Cell(l, 3), Cell(l, 4), Cell(l, 5) }), [2, 1.2, 1.2, .7, 1]));
        return Section("Spatial columns", panel, "Every geometry and geography column registered in this database.");
    }

    // ── TimescaleDB ─────────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> TimescaleInsight(Db active, CancellationToken ct)
    {
        var tables = Rows(await active.Query(
            """
            SELECT h.hypertable_schema || '.' || h.hypertable_name, h.num_chunks, h.compression_enabled, h.num_dimensions,
                   (SELECT count(*) FROM timescaledb_information.chunks c WHERE c.hypertable_schema = h.hypertable_schema AND c.hypertable_name = h.hypertable_name AND c.is_compressed)
            FROM timescaledb_information.hypertables h ORDER BY h.num_chunks DESC, 1 LIMIT 50
            """, ct, true)).ToList();
        // Job ids below 1000 are TimescaleDB's own (telemetry, job history retention); policies start at 1000.
        // job_stats only lists jobs that have run, and last_run_status is 'Success' or 'Failed'.
        var jobs = Rows(await active.Query(
            """
            SELECT count(*), count(*) FILTER (WHERE j.job_id >= 1000), count(*) FILTER (WHERE s.last_run_status = 'Failed')
            FROM timescaledb_information.jobs j LEFT JOIN timescaledb_information.job_stats s ON s.job_id = j.job_id
            """, ct, true)).First();
        double compressedChunks = tables.Sum(t => Number(t, 4)), builtIn = Number(jobs, 0) - Number(jobs, 1), failedJobs = Number(jobs, 2);
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Flow(150,
            Tile("Hypertables", tables.Count.ToString(), out _, tables.Count == 50 ? "first 50 shown" : ""),
            Tile("Chunks", Whole(tables.Sum(t => Number(t, 1))), out _, Whole(compressedChunks) + " compressed"),
            Tile("Compression on", tables.Count(t => Cell(t, 2) == "t").ToString(), out _, tables.Count == 1 ? "of 1 hypertable" : $"of {tables.Count} hypertables"),
            Tile("Policies", Whole(Number(jobs, 1)), out _, (failedJobs > 0 ? Plural(failedJobs, "job", "jobs") + " failed last run · " : "") + (builtIn > 0 ? $"plus {Whole(builtIn)} built-in" : "background jobs"))));
        if (tables.Count == 0) { panel.Children.Add(Muted("No hypertables exist yet. Convert a time-partitioned table with create_hypertable().", 13)); return Section("Hypertables", panel); }
        panel.Children.Add(Bars(tables.Select(t => (Label: Cell(t, 0), Value: Number(t, 1), Display: Plural(Number(t, 1), "chunk", "chunks"),
            Detail: (string?)((Cell(t, 2) == "t" ? $"compression on · {Whole(Number(t, 4))} of {Plural(Number(t, 1), "chunk", "chunks")} compressed" : "compression off") + " · " + Cell(t, 3) + (Cell(t, 3) == "1" ? " dimension" : " dimensions")), Open: (Action?)null)).ToList(), Tone.Good));
        return Section("Hypertables", panel, "Hypertables ordered by how many chunks they hold.");
    }

    // ── pg_cron ─────────────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> CronInsight(Db active, CancellationToken ct)
    {
        // to_jsonb keeps this working on versions that predate the jobname column.
        var jobs = Rows(await active.Query("SELECT jobid, schedule, regexp_replace(command, '\\s+', ' ', 'g'), database, active, coalesce(to_jsonb(j) ->> 'jobname', '') FROM cron.job j ORDER BY jobid LIMIT 100", ct, true)).ToList();
        var runs = new List<DataRow>(); string runsNote = "";
        try { runs = Rows(await active.Query("SELECT jobid, status, to_char(start_time, 'YYYY-MM-DD HH24:MI:SS'), coalesce(left(return_message, 300), ''), extract(epoch FROM end_time - start_time) * 1000 FROM cron.job_run_details ORDER BY runid DESC LIMIT 600", ct, true)).ToList(); }
        catch (Npgsql.PostgresException error) { runsNote = "Run history is not readable: " + error.MessageText; }

        var panel = new StackPanel { Spacing = 14 };
        int failed = runs.Count(r => Cell(r, 1) == "failed");
        panel.Children.Add(Flow(150,
            Tile("Jobs", jobs.Count.ToString(), out _, jobs.Count(j => Cell(j, 4) == "t") + " active"),
            Tile("Recent runs", runs.Count.ToString(), out _, runs.Count == 600 ? "latest 600" : ""),
            Tile("Failed", failed.ToString(), out _, runs.Count == 0 ? "" : "of the recent runs")));
        if (runsNote.Length > 0) panel.Children.Add(Muted(runsNote, 12));
        // cron.job and cron.job_run_details have row-level security: other roles' jobs are invisible, not absent.
        var role = Rows(await active.Query("SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user", ct, true)).FirstOrDefault();
        bool seesAll = role != null && Cell(role, 0) == "t";
        if (!seesAll) panel.Children.Add(Muted($"pg_cron shows each role only its own jobs, so jobs scheduled by other roles are not listed for {active.Profile.Username}.", 12));
        if (jobs.Count == 0) { panel.Children.Add(Muted(seesAll ? "No jobs are scheduled. Add one with cron.schedule()." : "This role has no scheduled jobs. Add one with cron.schedule().", 13)); return Section("Scheduled jobs", panel); }

        foreach (var job in jobs)
        {
            string id = Cell(job, 0), name = Cell(job, 5), command = Cell(job, 2);
            var heading = Text(name.Length > 0 ? name : "Job " + id, 14); heading.FontSize = 14; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var sql = Muted(command.Length > 160 ? command[..160] + "…" : command, 12); sql.FontFamily = new FontFamily("Consolas"); sql.MaxLines = 2; sql.TextTrimming = TextTrimming.CharacterEllipsis; sql.IsTextSelectionEnabled = true;
            var facts = Row(Pill(Cell(job, 1), Tone.Accent), Pill(Cell(job, 4) == "t" ? "Active" : "Paused", Cell(job, 4) == "t" ? Tone.Good : Tone.Neutral), Pill(Cell(job, 3)));
            // Most recent run on the right, oldest on the left, like a timeline.
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var run in runs.Where(r => Cell(r, 0) == id).Take(30).Reverse())
            {
                string state = Cell(run, 1);
                var tone = state == "succeeded" ? Tone.Good : state == "failed" ? Tone.Warn : Tone.Accent;
                // Colour carries the state; in high contrast the shape does: filled succeeded, outlined failed, half-height running.
                var mark = Themed(new Border { Width = 10, Height = 22, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom }, (b, dark) =>
                {
                    if (!HighContrast) { b.BorderThickness = new Thickness(0); b.Height = 22; b.Background = new SolidColorBrush(ToneColor(tone, dark)); return; }
                    b.BorderBrush = SystemText(); b.BorderThickness = new Thickness(tone == Tone.Warn ? 2 : 0);
                    b.Background = tone == Tone.Warn ? null : SystemText(); b.Height = tone == Tone.Accent ? 11 : 22;
                });
                ToolTipService.SetToolTip(mark, $"{state} · {Cell(run, 2)}" + (Cell(run, 4).Length > 0 ? " · " + Duration(Number(run, 4)) : "") + (Cell(run, 3).Length > 0 ? "\n" + Cell(run, 3) : ""));
                AutomationProperties.SetName(mark, $"{state}, {Cell(run, 2)}");
                strip.Children.Add(mark);
            }
            var body = new StackPanel { Spacing = 8 }; body.Children.Add(heading); body.Children.Add(ScrollTools(facts)); body.Children.Add(sql);
            body.Children.Add(strip.Children.Count > 0 ? ScrollTools(strip) : Muted("No recorded runs.", 12));
            // The newest failure's message, so the reason is readable without hovering a bar.
            if (runs.FirstOrDefault(r => Cell(r, 0) == id) is { } latest && Cell(latest, 1) == "failed")
            {
                // pg_cron stores the server's "ERROR:  " prefix in return_message.
                string message = System.Text.RegularExpressions.Regex.Replace(Cell(latest, 3).ReplaceLineEndings(" "), @"^ERROR:\s+", "");
                var reason = Text("Last run failed" + (message.Length > 0 ? ": " + message : "."), 12);
                reason.TextWrapping = TextWrapping.Wrap; reason.IsTextSelectionEnabled = true;
                Themed(reason, (t, dark) => { if (HighContrast) t.Foreground = SystemText(); else t.Foreground = new SolidColorBrush(ToneColor(Tone.Warn, dark)); });
                body.Children.Add(reason);
            }
            panel.Children.Add(Surface(body, 14, 8));
        }
        return Section("Scheduled jobs", panel, "Each bar is one run, oldest to newest: teal succeeded, red failed, amber still running. Hover a bar for its time and message.");
    }

    // ── pg_trgm ─────────────────────────────────────────────────────────────────────────────────
    FrameworkElement TrigramInsight(ExtensionState state, Db active)
    {
        string schema = In(state);
        var first = new TextBox { Header = "First text", Text = "postgres", MaxLength = 200 };
        var second = new TextBox { Header = "Second text", Text = "postgers", MaxLength = 200 };
        var result = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        async Task Compare()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // chr(31) cannot appear in a trigram of ordinary text, so it is a safe separator.
                var row = Rows(await active.Query($"SELECT {schema}similarity(@a, @b), {schema}word_similarity(@a, @b), array_to_string({schema}show_trgm(@a), chr(31)), array_to_string({schema}show_trgm(@b), chr(31)), current_setting('pg_trgm.similarity_threshold', true)", timeout.Token, true, ("a", first.Text), ("b", second.Text))).First();
                var left = Cell(row, 2).Split('\u001f', StringSplitOptions.RemoveEmptyEntries); var right = Cell(row, 3).Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
                var shared = left.Intersect(right).ToHashSet();
                double similarity = Number(row, 0), threshold = Number(row, 4);
                FrameworkElement Chips(string[] trigrams)
                {
                    var wrap = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemHeight = 30, ItemWidth = 58 };
                    foreach (var trigram in trigrams)
                    {
                        // Spaces are significant in trigrams; show them as a visible middle dot.
                        var chip = Pill(trigram.Replace(' ', '·'), shared.Contains(trigram) ? Tone.Good : Tone.Neutral);
                        ((TextBlock)chip.Child).FontFamily = new FontFamily("Consolas");
                        wrap.Children.Add(chip);
                    }
                    return wrap;
                }
                var verdict = threshold > 0 ? (similarity >= threshold ? $"At or above the % operator threshold ({threshold:0.##}), so these match." : $"Below the % operator threshold ({threshold:0.##}), so these do not match.") : "";
                result.Content = Stack(
                    Flow(150, Tile("Similarity", similarity.ToString("0.###"), out _, $"{shared.Count} shared of {left.Union(right).Count()} distinct trigrams"), Tile("Word similarity", Number(row, 1).ToString("0.###"), out _, "best match of the first text inside the second")),
                    Muted(verdict, 13),
                    Stack(Eyebrow("Trigrams of the first text"), Chips(left)), Stack(Eyebrow("Trigrams of the second text"), Chips(right)),
                    Muted("Shared trigrams are highlighted. A dot stands for the padding space Postgres adds around each word.", 12));
            }
            catch (Exception error) { result.Content = Muted("The comparison could not be run: " + Describe(error), 13); }
        }
        var compare = Button("Compare", async (_, _) => await Compare(), true);
        var panel = Stack(Flow(260, first, second), compare, result);
        panel.Loaded += async (_, _) => { if (result.Content == null) await Compare(); };
        return Section("Similarity playground", panel, "See how Postgres breaks text into trigrams and scores two strings. Runs similarity() on the server; nothing is stored.");
    }

    // ── pg_buffercache ──────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> BufferCacheInsight(ExtensionState state, Db active, CancellationToken ct)
    {
        string view = In(state) + "pg_buffercache";
        var totals = Rows(await active.Query($"SELECT count(*), count(relfilenode), count(*) FILTER (WHERE isdirty), current_setting('block_size')::int FROM {view}", ct, true)).First();
        var top = Rows(await active.Query(
            $"""
            SELECT n.nspname || '.' || c.relname, count(*), count(*) FILTER (WHERE b.isdirty), c.relkind
            FROM {view} b
            JOIN pg_class c ON b.relfilenode = pg_relation_filenode(c.oid)
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE b.reldatabase IN (0, (SELECT oid FROM pg_database WHERE datname = current_database()))
            GROUP BY 1, 4 ORDER BY 2 DESC LIMIT 15
            """, ct, true)).ToList();
        double block = Math.Max(Number(totals, 3), 1), all = Math.Max(Number(totals, 0), 1);
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Flow(150,
            Tile("Shared buffers", Bytes(Number(totals, 0) * block), out _, Whole(Number(totals, 0)) + " buffers on the server"),
            Tile("In use", (Number(totals, 1) / all).ToString("P0"), out _, Bytes(Number(totals, 1) * block) + ", all databases"),
            Tile("Dirty", Bytes(Number(totals, 2) * block), out _, "all databases, waiting to be written")));
        if (top.Count > 0)
            panel.Children.Add(Bars(top.Select(r => Bar(Cell(r, 0), Number(r, 1), Bytes(Number(r, 1) * block),
                $"{Number(r, 1) / all:P1} of the cache · {(Cell(r, 3) == "i" ? "index" : Cell(r, 3) == "t" ? "TOAST" : "table")}" + (Number(r, 2) > 0 ? $" · {Bytes(Number(r, 2) * block)} dirty" : ""))).ToList()));
        return Section("What is in memory", panel, "The totals cover the whole server; the list shows the relations of this database (and shared catalogs) holding the most buffers. A snapshot; it changes as queries run.");
    }

    // ── Foreign data wrappers ───────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> ForeignDataInsight(ExtensionState state, Db active, CancellationToken ct)
    {
        var servers = Rows(await active.Query(
            """
            SELECT s.srvname::text, coalesce(array_to_string(s.srvoptions, ', '), ''), (SELECT count(*) FROM pg_foreign_table t WHERE t.ftserver = s.oid)::text
            FROM pg_foreign_server s JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw
            WHERE w.fdwname = @name ORDER BY 1 LIMIT 100
            """, ct, true, ("name", state.Name))).ToList();
        var tables = Rows(await active.Query(
            """
            SELECT n.nspname || '.' || c.relname, s.srvname::text, coalesce(array_to_string(t.ftoptions, ', '), '')
            FROM pg_foreign_table t
            JOIN pg_class c ON c.oid = t.ftrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_foreign_server s ON s.oid = t.ftserver JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw
            WHERE w.fdwname = @name ORDER BY 1 LIMIT 100
            """, ct, true, ("name", state.Name))).ToList();
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Flow(150, Tile("Foreign servers", servers.Count.ToString(), out _), Tile("Foreign tables", tables.Count.ToString(), out _, tables.Count == 100 ? "first 100 shown" : "")));
        // Several wrappers share this page, so each section names its own.
        if (servers.Count == 0) { panel.Children.Add(Muted($"No foreign server uses {state.Title} yet. Define one with CREATE SERVER … FOREIGN DATA WRAPPER {state.Name}.", 13)); return Section(state.Title + " servers and tables", panel); }
        panel.Children.Add(MiniTable(["Server", "Options", "Tables"], servers.Select(s => new[] { Cell(s, 0), Cell(s, 1).Length > 0 ? Cell(s, 1) : "none", Cell(s, 2) }), [1, 2.4, .6]));
        if (tables.Count > 0) panel.Children.Add(MiniTable(["Foreign table", "Server", "Options"], tables.Take(40).Select(t => new[] { Cell(t, 0), Cell(t, 1), Cell(t, 2) }), [1.6, 1, 2]));
        return Section(state.Title + " servers and tables", panel, "Server options are shown as stored. Passwords live in user mappings and are never read here.");
    }

    // ── PGMQ ────────────────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> QueueInsight(Db active, CancellationToken ct)
    {
        // queue_visible_length only exists from PGMQ 1.5; read it through to_jsonb so older versions still work.
        // queue_length counts messages a consumer has read but not yet deleted or archived ("in flight").
        var queues = Rows(await active.Query("SELECT m.queue_name::text, m.queue_length, coalesce(m.oldest_msg_age_sec, 0), m.total_messages, to_jsonb(m)->>'queue_visible_length' FROM pgmq.metrics_all() m ORDER BY m.queue_length DESC LIMIT 100", ct, true)).ToList();
        static double InFlight(DataRow q) => Cell(q, 4) == "" ? 0 : Math.Max(0, Number(q, 1) - Number(q, 4));
        var panel = new StackPanel { Spacing = 14 };
        double inFlight = queues.Sum(InFlight);
        panel.Children.Add(Flow(150, Tile("Queues", queues.Count.ToString(), out _), Tile("Messages in queues", Whole(queues.Sum(q => Number(q, 1))), out _, inFlight > 0 ? $"{Whole(inFlight)} read, not yet deleted" : "")));
        if (queues.Count == 0) { panel.Children.Add(Muted("No queues exist yet. Create one with pgmq.create().", 13)); return Section("Queues", panel); }
        panel.Children.Add(Bars(queues.Select(q => (Label: Cell(q, 0), Value: Number(q, 1), Display: Whole(Number(q, 1)) + " in queue",
            // oldest_msg_age_sec is NULL for an empty queue; "0 ms old" would read as a fresh message.
            Detail: (string?)((Number(q, 1) > 0 ? $"oldest message {Duration(Number(q, 2) * 1000)} old" : "empty") + (InFlight(q) > 0 ? $" · {Whole(InFlight(q))} read, not yet deleted" : "") + $" · {Whole(Number(q, 3))} sent in total"), Open: (Action?)null)).ToList()));
        return Section("Queues", panel, "Queue depth right now, deepest first. Read messages stay in the queue, hidden from other readers, until they are deleted or archived.");
    }

    // ── Citus ───────────────────────────────────────────────────────────────────────────────────
    async Task<FrameworkElement> CitusInsight(Db active, CancellationToken ct)
    {
        // Group 0 is the coordinator; it holds shards only when shouldhaveshards is on (single-node clusters).
        var nodes = Rows(await active.Query("SELECT nodename::text, nodeport::text, CASE WHEN groupid = 0 THEN 'coordinator' ELSE 'worker' END || CASE WHEN noderole::text <> 'primary' THEN ' (' || noderole::text || ')' ELSE '' END || CASE WHEN shouldhaveshards THEN '' ELSE ', no shards' END, isactive FROM pg_dist_node ORDER BY groupid, 1, 2 LIMIT 100", ct, true)).ToList();
        // citus_tables already formats table_size as text ("16 kB"), so it is shown as is and cannot order the list.
        var tables = Rows(await active.Query("SELECT table_name::text, citus_table_type::text, distribution_column::text, shard_count, table_size FROM citus_tables ORDER BY 1 LIMIT 50", ct, true)).ToList();
        var panel = new StackPanel { Spacing = 14 };
        int workers = nodes.Count(n => Cell(n, 2).StartsWith("worker"));
        panel.Children.Add(Flow(150, Tile("Nodes", nodes.Count.ToString(), out _, $"{nodes.Count(n => Cell(n, 3) == "t")} active · {Plural(workers, "worker", "workers")}"), Tile("Citus tables", tables.Count.ToString(), out _, tables.Count == 50 ? "first 50 shown" : "")));
        if (nodes.Count == 0) panel.Children.Add(Muted("No nodes are registered. On a coordinator, add workers with citus_add_node().", 13));
        if (nodes.Count > 0) panel.Children.Add(MiniTable(["Node", "Port", "Role", "Active"], nodes.Select(n => new[] { Cell(n, 0), Cell(n, 1), Cell(n, 2), Cell(n, 3) == "t" ? "Yes" : "No" })));
        if (tables.Count > 0) panel.Children.Add(MiniTable(["Table", "Type", "Distribution column", "Shards", "Size"], tables.Select(t => new[] { Cell(t, 0), Cell(t, 1), Cell(t, 2), Cell(t, 3), Cell(t, 4) }), [1.6, 1, 1.2, .6, .8]));
        return Section("Cluster", panel, "The coordinator and worker nodes, and the tables Citus manages, by name.");
    }
}
