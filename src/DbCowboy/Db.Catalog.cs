using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace DbCowboy;

public sealed partial class Db
{
    public async Task<List<ColumnInfo>> Columns(Relation relation, CancellationToken ct)
    {
        var r = await Query("SELECT a.attname,format_type(a.atttypid,a.atttypmod),NOT a.attnotnull,(a.attgenerated<>'' OR a.attidentity<>''),pg_get_expr(d.adbin,d.adrelid) FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum WHERE a.attrelid=to_regclass(@r) AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum", ct, true, ("r", relation.Qualified));
        return r.Table.Rows.Cast<DataRow>().Select(row => new ColumnInfo((string)row[0], (string)row[1], (string)row[2] == "t", (string)row[3] == "t", row[4] == DBNull.Value ? null : (string)row[4])).ToList();
    }
    public async Task<List<Completion>> Completions(CancellationToken ct)
    {
        var r = await Query("SELECT table_schema,table_name,column_name FROM information_schema.columns WHERE table_schema NOT IN('pg_catalog','information_schema') AND pg_catalog.has_schema_privilege(table_schema,'USAGE') ORDER BY table_schema,table_name,ordinal_position LIMIT 1900", ct, true);
        var completions = new List<Completion>();
        foreach (DataRow row in r.Table.Rows)
        {
            string schema = (string)row[0], table = (string)row[1], column = (string)row[2];
            completions.Add(new(schema + "." + table, Q(schema) + "." + Q(table), "table"));
            completions.Add(new(table, Q(schema) + "." + Q(table), "table"));
            completions.Add(new(column, Q(column), table + " column"));
            completions.Add(new(table + "." + column, Q(table) + "." + Q(column), "column"));
        }
        return completions.DistinctBy(x => (x.Label, x.Insert)).ToList();
    }
    public async Task<List<ForeignKeyInfo>> ForeignKeys(CancellationToken ct)
    {
        var r = await Query("SELECT co.conname,ns.nspname,sc.relname,(SELECT json_agg(a.attname ORDER BY k.ord)::text FROM unnest(co.conkey) WITH ORDINALITY k(num,ord) JOIN pg_attribute a ON a.attrelid=co.conrelid AND a.attnum=k.num),nt.nspname,tc.relname,(SELECT json_agg(a.attname ORDER BY k.ord)::text FROM unnest(co.confkey) WITH ORDINALITY k(num,ord) JOIN pg_attribute a ON a.attrelid=co.confrelid AND a.attnum=k.num) FROM pg_constraint co JOIN pg_class sc ON sc.oid=co.conrelid JOIN pg_namespace ns ON ns.oid=sc.relnamespace JOIN pg_class tc ON tc.oid=co.confrelid JOIN pg_namespace nt ON nt.oid=tc.relnamespace WHERE co.contype='f' AND ns.nspname NOT IN('pg_catalog','information_schema') AND pg_catalog.has_schema_privilege(ns.oid,'USAGE') AND pg_catalog.has_schema_privilege(nt.oid,'USAGE') AND pg_catalog.has_table_privilege(sc.oid,'SELECT') AND pg_catalog.has_table_privilege(tc.oid,'SELECT') ORDER BY 2,3,1 LIMIT 1000", ct, true);
        return r.Table.Rows.Cast<DataRow>().Select(row => new ForeignKeyInfo((string)row[0], (string)row[1], (string)row[2], JsonSerializer.Deserialize<string[]>((string)row[3])!, (string)row[4], (string)row[5], JsonSerializer.Deserialize<string[]>((string)row[6])!)).ToList();
    }
    /// <summary>Apply every staged mutation in one transaction; stale/multi-match operations abort the batch.</summary>
    public async Task CommitChanges(IReadOnlyList<PendingChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return;
        if (changes.Count > 1000) throw new InvalidOperationException("Apply at most 1,000 changes in one batch.");
        var types = new Dictionary<string, List<ColumnInfo>>();
        foreach (var relation in changes.Select(c => c.Relation).Distinct()) types[relation.Qualified] = await Columns(relation, ct);
        await using var c = await source.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using (var setup = new NpgsqlCommand("SET LOCAL statement_timeout='30s';SET LOCAL lock_timeout='5s'", c, tx)) await setup.ExecuteNonQueryAsync(ct);
        foreach (var change in changes)
        {
            var columns = types[change.Relation.Qualified]; var parameters = new List<NpgsqlParameter>();
            string Value(string name, string? value)
            {
                var col = columns.Single(x => x.Name == name); if (col.Generated) throw new InvalidOperationException($"{name} is generated or identity and cannot be assigned.");
                string parameter = "p" + parameters.Count; parameters.Add(new(parameter, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value });
                return $"CAST(@{parameter} AS {col.Type})";
            }
            string Where()
            {
                if (change.Original.Count == 0) throw new InvalidOperationException("An original row snapshot is required.");
                return string.Join(" AND ", change.Original.Select(pair => { string name = "p" + parameters.Count; parameters.Add(new(name, NpgsqlDbType.Text) { Value = pair.Value is null ? DBNull.Value : pair.Value }); return $"{Q(pair.Key)}::text IS NOT DISTINCT FROM @{name}"; }));
            }
            string sql;
            if (change.Kind == ChangeKind.Insert) sql = change.Values.Count == 0 ? $"INSERT INTO {change.Relation.Qualified} DEFAULT VALUES RETURNING 1" : $"INSERT INTO {change.Relation.Qualified} ({string.Join(",", change.Values.Keys.Select(Q))}) VALUES ({string.Join(",", change.Values.Select(pair => Value(pair.Key, pair.Value)))}) RETURNING 1";
            else if (change.Kind == ChangeKind.Update) { if (change.Values.Count == 0) continue; string set = string.Join(",", change.Values.Select(pair => $"{Q(pair.Key)}={Value(pair.Key, pair.Value)}")); sql = $"UPDATE {change.Relation.Qualified} SET {set} WHERE {Where()} RETURNING 1"; }
            else sql = $"DELETE FROM {change.Relation.Qualified} WHERE {Where()} RETURNING 1";
            await using var cmd = new NpgsqlCommand(sql, c, tx); cmd.Parameters.AddRange(parameters.ToArray());
            int count = 0; await using (var reader = await cmd.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) count++;
            if (count != 1) throw new InvalidOperationException($"{change.Kind} matched {count} rows; a row may have changed. The entire batch was rolled back.");
        }
        await tx.CommitAsync(ct);
    }
}
