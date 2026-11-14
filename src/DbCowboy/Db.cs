using System.Data;
using System.Globalization;
using System.Text;
using Npgsql;
namespace DbCowboy;

public sealed record Relation(string Schema, string Name, string Kind, long Estimate) { public string Qualified => $"{Db.Q(Schema)}.{Db.Q(Name)}"; public override string ToString() => Name; }
public sealed record VectorColumn(string Schema, string Table, string Column, string Type, string TypeSchema = "public") { public string Qualified => $"{Db.Q(Schema)}.{Db.Q(Table)}"; public string CastType => $"{Db.Q(TypeSchema)}.{Db.Q(Type)}"; public override string ToString() => $"{Schema}.{Table} · {Column} ({Type})"; }
public sealed record QueryResult(DataTable Table, int Affected, bool Truncated, IReadOnlyList<DataTable>? Sets = null) { public IReadOnlyList<DataTable> Results => Sets ?? [Table]; }
public sealed partial class Db : IAsyncDisposable
{
    public Guid SessionIdentity { get; } = Guid.NewGuid();
    readonly NpgsqlDataSource source; public Profile Profile { get; }
    readonly SshTunnel? tunnel;
    /// <summary>The SSH server's host key as seen by this session's tunnel; null without a tunnel.</summary>
    public string? TunnelHostKey => tunnel?.HostKey;
    /// <summary>A session, optionally through an SSH tunnel that it then owns and closes with itself.</summary>
    public Db(Profile profile, string password, SshTunnel? tunnel = null)
    {
        Profile = profile; this.tunnel = tunnel;
        var b = new NpgsqlDataSourceBuilder(profile.ConnectionString(password, tunnel == null ? null : ("127.0.0.1", tunnel.LocalPort)));
        b.UseSslClientAuthenticationOptionsCallback(options =>
        {
            // .NET on Windows caches TLS credentials for the whole process, and a connection with no client certificate
            // can pick up the credential of an earlier connection that had one, so it would present a certificate
            // applied to a different connection. Whether sessions may resume is part of what the cache matches on,
            // so connections with a certificate are kept apart from those without by giving them a different setting.
            options.AllowTlsResume = string.IsNullOrWhiteSpace(profile.ClientCertificate);
            // Through a tunnel the socket goes to 127.0.0.1, but the certificate belongs to the real server, so TLS names and verifies that host.
            if (tunnel != null) options.TargetHost = profile.Host;
        });
        b.EnableUnmappedTypes(); source = b.Build();
    }
    /// <summary>Opens the profile's SSH tunnel when it has one, then the session over it.</summary>
    public static async Task<Db> Connect(Profile profile, string password, CancellationToken ct)
    {
        var tunnel = profile.Ssh ? await SshTunnel.Open(profile, ct) : null;
        try { return new Db(profile, password, tunnel); } catch { tunnel?.Dispose(); throw; }
    }
    public static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    public async Task<string> Test(CancellationToken ct) { await using var c = await source.OpenConnectionAsync(ct); return c.PostgreSqlVersion.ToString(); }
    public async Task<QueryResult> Query(string sql, CancellationToken ct, bool? readOnly = null, params (string Name, object Value)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using (var setup = new NpgsqlCommand((readOnly ?? Profile.ReadOnly) ? "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '60s'; SET LOCAL lock_timeout = '5s'" : "SET LOCAL statement_timeout = '60s'; SET LOCAL lock_timeout = '5s'", c, tx)) await setup.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c, tx); cmd.AllResultTypesAreUnknown = true; foreach (var a in args) cmd.Parameters.AddWithValue(a.Name, a.Value);
        var table = new DataTable(); var sets = new List<DataTable>(); bool truncated = false; int affected = 0; int resultNumber = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            do
            {
                var current = new DataTable("Result " + (++resultNumber)); for (int i = 0; i < r.FieldCount; i++) { var original = r.GetName(i); var name = original; int suffix = 1; while (current.Columns.Contains(name)) name = original + "_" + suffix++; current.Columns.Add(name, typeof(string)); }
                int rows = 0; while (await r.ReadAsync(ct)) { if (rows++ >= 2000) { truncated = true; continue; } var values = new object[r.FieldCount]; for (int i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? DBNull.Value : r.GetString(i); current.Rows.Add(values); }
                if (current.Columns.Count > 0) { table = current; sets.Add(current); if (sets.Count > 25) throw new InvalidOperationException("Run at most 25 result-producing statements at once."); }
            } while (await r.NextResultAsync(ct)); affected = r.RecordsAffected;
        }
        await tx.CommitAsync(ct); return new(table, Math.Max(0, affected), truncated, sets);
    }
    public async Task<List<Relation>> Relations(CancellationToken ct) { var r = await Query("SELECT n.nspname,c.relname,c.relkind,c.reltuples::bigint FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind IN ('r','p','v','m') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND pg_catalog.has_schema_privilege(n.oid,'USAGE') ORDER BY 1,2", ct, true); return r.Table.Rows.Cast<DataRow>().Select(x => new Relation((string)x[0], (string)x[1], (string)x[2], long.Parse((string)x[3]))).ToList(); }
    public async Task<List<VectorColumn>> Vectors(CancellationToken ct) { var r = await Query("SELECT n.nspname,c.relname,a.attname,t.typname,tn.nspname FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_type t ON t.oid=a.atttypid JOIN pg_namespace tn ON tn.oid=t.typnamespace WHERE t.typname IN ('vector','halfvec') AND a.attnum>0 AND NOT a.attisdropped AND c.relkind IN ('r','p','m','v') AND pg_catalog.has_schema_privilege(n.oid,'USAGE') AND pg_catalog.has_table_privilege(c.oid,'SELECT') ORDER BY 1,2,3", ct, true); return r.Table.Rows.Cast<DataRow>().Select(x => new VectorColumn((string)x[0], (string)x[1], (string)x[2], (string)x[3], (string)x[4])).ToList(); }
    public async ValueTask DisposeAsync() { await source.DisposeAsync(); tunnel?.Dispose(); }
    public static void Csv(DataTable table, string path) { using var w = new StreamWriter(path, false, new UTF8Encoding(true)); string Esc(object? v) => "\"" + (v == DBNull.Value ? "" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\""; w.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(c => Esc(c.ColumnName)))); foreach (DataRow r in table.Rows) w.WriteLine(string.Join(",", r.ItemArray.Select(Esc))); }
}

