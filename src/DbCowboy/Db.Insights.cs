using System.Data;
using System.Globalization;

namespace DbCowboy;

/// <summary>Size and maintenance facts for one table, read from the catalog and the statistics views.</summary>
public sealed record TableFacts(string TotalSize, string TableSize, string IndexSize, int Indexes, long LiveRows, long DeadRows, string? LastAnalyzed, string? LastVacuumed);

public sealed record SizedRelation(string Schema, string Name, long Bytes, string Size, long Rows);

/// <summary>An index the statistics collector has never seen used since the counters were last reset.</summary>
public sealed record IdleIndex(string Schema, string Table, string Index, string Size);

public sealed record DatabaseInsights(IReadOnlyList<SizedRelation> LargestTables, IReadOnlyList<IdleIndex> IdleIndexes, string? StatsReset);

/// <summary>
/// Read-only catalog summaries. Every query runs in a read-only transaction, is bounded with LIMIT and
/// only reports relations the role may read. Nothing here estimates bloat or guesses: the numbers are
/// what Postgres itself records.
/// </summary>
public sealed partial class Db
{
    static long Whole(object value) => value == DBNull.Value ? 0 : (long)double.Parse((string)value, CultureInfo.InvariantCulture);
    static string? Optional(object value) => value == DBNull.Value ? null : (string)value;

    public async Task<TableFacts?> TableFacts(Relation relation, CancellationToken ct)
    {
        var result = await Query(
            """
            SELECT pg_size_pretty(pg_total_relation_size(c.oid)), pg_size_pretty(pg_table_size(c.oid)), pg_size_pretty(pg_indexes_size(c.oid)),
                   (SELECT count(*) FROM pg_index x WHERE x.indrelid = c.oid),
                   coalesce(s.n_live_tup, 0), coalesce(s.n_dead_tup, 0),
                   to_char(greatest(s.last_analyze, s.last_autoanalyze), 'YYYY-MM-DD HH24:MI'),
                   to_char(greatest(s.last_vacuum, s.last_autovacuum), 'YYYY-MM-DD HH24:MI')
            FROM pg_class c LEFT JOIN pg_stat_all_tables s ON s.relid = c.oid
            WHERE c.oid = to_regclass(@r)
            """, ct, true, ("r", relation.Qualified));
        if (result.Table.Rows.Count == 0) return null;
        var row = result.Table.Rows[0];
        var facts = new TableFacts((string)row[0], (string)row[1], (string)row[2], (int)Whole(row[3]), Whole(row[4]), Whole(row[5]), Optional(row[6]), Optional(row[7]));
        if (relation.Kind != "p") return facts;
        // A partitioned table stores nothing itself (pg_partman parents, for one): its size and rows are the sum of its
        // partitions. pg_partition_tree exists from PostgreSQL 12; on older servers the parent's own numbers stay.
        try
        {
            var sum = await Query(
                """
                SELECT pg_size_pretty(sum(pg_total_relation_size(p.relid))), pg_size_pretty(sum(pg_table_size(p.relid))), pg_size_pretty(sum(pg_indexes_size(p.relid))),
                       coalesce(sum(s.n_live_tup), 0), coalesce(sum(s.n_dead_tup), 0)
                FROM pg_partition_tree(to_regclass(@r)) p LEFT JOIN pg_stat_all_tables s ON s.relid = p.relid
                WHERE p.isleaf
                """, ct, true, ("r", relation.Qualified));
            var total = sum.Table.Rows[0];
            if (total[0] == DBNull.Value) return facts;   // no partitions yet
            return facts with { TotalSize = (string)total[0], TableSize = (string)total[1], IndexSize = (string)total[2], LiveRows = Whole(total[3]), DeadRows = Whole(total[4]) };
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "42883") { return facts; }
    }

    /// <summary>The largest tables and the non-unique indexes that have never been scanned.</summary>
    public async Task<DatabaseInsights> Insights(CancellationToken ct)
    {
        var tables = await Query(
            """
            SELECT n.nspname, c.relname, pg_total_relation_size(c.oid), pg_size_pretty(pg_total_relation_size(c.oid)), greatest(c.reltuples, 0)::bigint
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'm') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_toast'
              AND has_schema_privilege(n.oid, 'USAGE') AND has_table_privilege(c.oid, 'SELECT')
            ORDER BY 3 DESC, 1, 2 LIMIT 6
            """, ct, true);
        // Unique and primary-key indexes enforce constraints whether or not they are scanned, so they are left out.
        var idle = await Query(
            """
            SELECT n.nspname, t.relname, i.relname, pg_size_pretty(pg_relation_size(i.oid))
            FROM pg_stat_user_indexes s
            JOIN pg_index x ON x.indexrelid = s.indexrelid
            JOIN pg_class i ON i.oid = s.indexrelid
            JOIN pg_class t ON t.oid = s.relid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE s.idx_scan = 0 AND NOT x.indisunique AND NOT x.indisprimary AND x.indisvalid AND has_table_privilege(t.oid, 'SELECT')
            ORDER BY pg_relation_size(i.oid) DESC, 1, 2, 3 LIMIT 6
            """, ct, true);
        var reset = await Query("SELECT to_char(stats_reset, 'YYYY-MM-DD HH24:MI') FROM pg_stat_database WHERE datname = current_database()", ct, true);
        return new(
            tables.Table.Rows.Cast<DataRow>().Select(r => new SizedRelation((string)r[0], (string)r[1], Whole(r[2]), (string)r[3], Whole(r[4]))).ToList(),
            idle.Table.Rows.Cast<DataRow>().Select(r => new IdleIndex((string)r[0], (string)r[1], (string)r[2], (string)r[3])).ToList(),
            reset.Table.Rows.Count == 0 ? null : Optional(reset.Table.Rows[0][0]));
    }

    /// <summary>Every index on a table with its method, size and how often it has been scanned.</summary>
    public Task<QueryResult> IndexUsage(Relation relation, CancellationToken ct) => Query(
        """
        SELECT i.relname AS "Index", am.amname AS "Method", pg_size_pretty(pg_relation_size(i.oid)) AS "Size",
               coalesce(s.idx_scan, 0) AS "Scans", coalesce(s.idx_tup_read, 0) AS "Entries read",
               CASE WHEN x.indisprimary THEN 'primary key' WHEN x.indisunique THEN 'unique' ELSE '' END AS "Enforces",
               CASE WHEN x.indisvalid THEN 'yes' ELSE 'no' END AS "Valid",
               pg_get_indexdef(i.oid) AS "Definition"
        FROM pg_index x
        JOIN pg_class i ON i.oid = x.indexrelid
        JOIN pg_am am ON am.oid = i.relam
        LEFT JOIN pg_stat_all_indexes s ON s.indexrelid = i.oid
        WHERE x.indrelid = to_regclass(@r)
        ORDER BY 1
        """, ct, true, ("r", relation.Qualified));
}
