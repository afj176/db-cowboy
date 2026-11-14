using System.Data;

namespace DbCowboy;

public sealed record DatabaseOverview(string Database, string Size, string Version, int Tables, int VectorColumns, int Extensions);

public sealed partial class Db
{
    /// <summary>Every extension the server can install plus any installed without a control file entry.</summary>
    public async Task<List<ExtensionState>> Extensions(CancellationToken ct)
    {
        var result = await Query(
            """
            SELECT coalesce(a.name, e.extname), a.default_version, e.extversion, n.nspname, coalesce(a.comment, ''),
                   coalesce((SELECT array_to_string(v.requires, ',') FROM pg_available_extension_versions v WHERE v.name = a.name AND v.version = a.default_version), '')
            FROM pg_available_extensions a
            FULL JOIN pg_extension e ON e.extname = a.name
            LEFT JOIN pg_namespace n ON n.oid = e.extnamespace
            ORDER BY 1
            """, ct, true);
        string? Optional(object value) => value == DBNull.Value ? null : (string)value;
        var server = result.Table.Rows.Cast<DataRow>().Select(row =>
            new ExtensionState((string)row[0], Optional(row[1]), Optional(row[2]), Optional(row[3]), (string)row[4], PgExtensions.Guide((string)row[0]))
            { Requires = ((string)row[5]).Split(',', StringSplitOptions.RemoveEmptyEntries) });
        return PgExtensions.Merge(server);
    }

    /// <summary>Configuration parameters registered under the extension's prefixes. Empty until its library is loaded.</summary>
    public async Task<List<ExtensionSetting>> ExtensionSettings(string extension, CancellationToken ct)
    {
        // Underscore is a LIKE wildcard; escape it so "pg_cron" does not match unrelated names.
        var patterns = PgExtensions.SettingPrefixes(extension).Select(p => p.Replace("_", "\\_") + ".%").ToArray();
        var result = await Query(
            "SELECT name, setting, coalesce(unit, ''), coalesce(short_desc, ''), context FROM pg_settings WHERE name LIKE ANY(@patterns) ORDER BY name LIMIT 200",
            ct, true, ("patterns", patterns));
        return result.Table.Rows.Cast<DataRow>().Select(r => new ExtensionSetting((string)r[0], (string)r[1], (string)r[2], (string)r[3], (string)r[4])).ToList();
    }

    /// <summary>What the extension added to this database and which user columns and indexes rely on it.</summary>
    public async Task<ExtensionFootprint> ExtensionFootprint(string extension, CancellationToken ct)
    {
        var counts = await Query(
            """
            SELECT count(*) FILTER (WHERE d.classid = 'pg_proc'::regclass),
                   count(*) FILTER (WHERE d.classid = 'pg_type'::regclass),
                   count(*) FILTER (WHERE d.classid = 'pg_operator'::regclass),
                   count(*) FILTER (WHERE d.classid = 'pg_am'::regclass),
                   count(*) FILTER (WHERE d.classid = 'pg_class'::regclass)
            FROM pg_depend d
            JOIN pg_extension e ON e.oid = d.refobjid AND d.refclassid = 'pg_extension'::regclass
            WHERE e.extname = @name AND d.deptype = 'e'
            """, ct, true, ("name", extension));
        var columns = await Query(
            """
            SELECT n.nspname || '.' || c.relname, a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_type element ON t.typcategory = 'A' AND element.oid = t.typelem
            JOIN pg_depend d ON d.classid = 'pg_type'::regclass AND d.objid = coalesce(element.oid, t.oid)
                            AND d.deptype = 'e' AND d.refclassid = 'pg_extension'::regclass
            JOIN pg_extension e ON e.oid = d.refobjid
            WHERE e.extname = @name AND a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'm')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1, 2 LIMIT 200
            """, ct, true, ("name", extension));
        var indexes = await Query(
            """
            SELECT DISTINCT n.nspname || '.' || t.relname, i.relname, am.amname || ' · ' || pg_size_pretty(pg_relation_size(i.oid))
            FROM pg_index x
            JOIN pg_class i ON i.oid = x.indexrelid
            JOIN pg_class t ON t.oid = x.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am am ON am.oid = i.relam
            JOIN pg_opclass oc ON oc.oid = ANY (x.indclass::oid[])
            JOIN pg_depend d ON d.deptype = 'e' AND d.refclassid = 'pg_extension'::regclass
                            AND ((d.classid = 'pg_opclass'::regclass AND d.objid = oc.oid) OR (d.classid = 'pg_am'::regclass AND d.objid = am.oid))
            JOIN pg_extension e ON e.oid = d.refobjid
            WHERE e.extname = @name AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1, 2 LIMIT 200
            """, ct, true, ("name", extension));
        int Count(int column) => counts.Table.Rows.Count == 0 ? 0 : int.Parse((string)counts.Table.Rows[0][column]);
        List<ExtensionUse> Uses(DataTable table) => table.Rows.Cast<DataRow>().Select(r => new ExtensionUse((string)r[0], (string)r[1], (string)r[2])).ToList();
        return new(Count(0), Count(1), Count(2), Count(3), Count(4), Uses(columns.Table), Uses(indexes.Table));
    }

    /// <summary>Extension DDL always runs read-write. DROP never cascades, so dependent objects make it fail visibly.</summary>
    public static string InstallExtensionSql(string name, bool withRequired) => $"CREATE EXTENSION IF NOT EXISTS {Q(name)}" + (withRequired ? " CASCADE" : "");
    public Task InstallExtension(string name, bool withRequired, CancellationToken ct) => Query(InstallExtensionSql(name, withRequired), ct, false);
    public Task UpdateExtension(string name, CancellationToken ct) => Query($"ALTER EXTENSION {Q(name)} UPDATE", ct, false);
    public Task DropExtension(string name, CancellationToken ct) => Query($"DROP EXTENSION {Q(name)}", ct, false);

    public async Task<DatabaseOverview> Overview(CancellationToken ct)
    {
        var result = await Query(
            """
            SELECT current_database(),
                   pg_size_pretty(pg_database_size(current_database())),
                   current_setting('server_version'),
                   (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'),
                   (SELECT count(*) FROM pg_attribute a JOIN pg_type t ON t.oid = a.atttypid JOIN pg_class c ON c.oid = a.attrelid
                     WHERE t.typname IN ('vector', 'halfvec', 'sparsevec') AND a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'm')),
                   (SELECT count(*) FROM pg_extension)
            """, ct, true);
        var row = result.Table.Rows[0];
        return new((string)row[0], (string)row[1], (string)row[2], int.Parse((string)row[3]), int.Parse((string)row[4]), int.Parse((string)row[5]));
    }
}
