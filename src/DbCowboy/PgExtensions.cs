namespace DbCowboy;

/// <summary>What DB Cowboy knows about a Postgres extension before asking any server.</summary>
/// <param name="Insight">Key of the dedicated visual, or "" when only the shared footprint view applies.</param>
/// <param name="Preload">True when the library must be listed in shared_preload_libraries before CREATE EXTENSION is useful.</param>
public sealed record ExtensionGuide(string Name, string Title, string Category, string Summary, string Docs, string Insight = "", bool Preload = false);

/// <summary>One extension as the connected server reports it, joined with the local guide when one exists.</summary>
public sealed record ExtensionState(string Name, string? DefaultVersion, string? InstalledVersion, string? Schema, string Comment, ExtensionGuide? Guide)
{
    /// <summary>Extensions the server says must be installed first, for the default version.</summary>
    public string[] Requires { get; init; } = [];
    public bool Installed => InstalledVersion != null;
    public bool Available => DefaultVersion != null;
    public bool Outdated => Installed && Available && InstalledVersion != DefaultVersion;
    public string Title => Guide?.Title ?? Name;
    public string Category => Guide?.Category ?? "Other";
    public string Summary => Guide?.Summary ?? (Comment.Length > 0 ? Comment : "No description is published for this extension.");
}

/// <summary>A page that appears in the navigation while an extension behind it is installed in the connected database.</summary>
public sealed record ExtensionPageInfo(string Key, string Label, string Description);

public sealed record ExtensionSetting(string Name, string Value, string Unit, string Description, string Context);
public sealed record ExtensionFootprint(int Functions, int Types, int Operators, int AccessMethods, int Tables, IReadOnlyList<ExtensionUse> Columns, IReadOnlyList<ExtensionUse> Indexes);
/// <summary>A user object that depends on an extension: a column of one of its types, or an index using its operator classes.</summary>
public sealed record ExtensionUse(string Relation, string Object, string Detail);

public static class PgExtensions
{
    public const string Vectors = "Vectors & search", Geo = "Geospatial", Time = "Time series & scale", Monitor = "Monitoring", Text = "Text & matching",
        Types = "Data types", Indexes = "Indexes", Foreign = "Foreign data", Security = "Security", Admin = "Maintenance", Jobs = "Jobs & queues", Languages = "Languages", Analytics = "Analytics & graph";

    public static readonly string[] Categories = [Vectors, Geo, Time, Monitor, Text, Types, Indexes, Foreign, Security, Admin, Jobs, Languages, Analytics];

    static string Core(string page) => "https://www.postgresql.org/docs/current/" + page + ".html";

    /// <summary>Widely used extensions. A server may offer others; those still appear, described by the server's own comment.</summary>
    public static readonly ExtensionGuide[] Guides =
    [
        new("vector", "pgvector", Vectors, "Vector columns with exact and approximate nearest-neighbour search (HNSW, IVFFlat).", "https://github.com/pgvector/pgvector", "vector"),
        new("vectorscale", "pgvectorscale", Vectors, "StreamingDiskANN indexes and compression for large pgvector collections.", "https://github.com/timescale/pgvectorscale"),
        new("pg_search", "ParadeDB pg_search", Vectors, "BM25 full-text search indexes built on Tantivy.", "https://docs.paradedb.com"),
        new("rum", "RUM", Vectors, "Inverted index that stores positions for faster ranked full-text search.", "https://github.com/postgrespro/rum"),
        new("pgroonga", "PGroonga", Vectors, "Fast full-text search for all languages, backed by Groonga.", "https://pgroonga.github.io"),

        new("postgis", "PostGIS", Geo, "Geometry and geography types, spatial indexes and hundreds of spatial functions.", "https://postgis.net/documentation/", "postgis"),
        new("postgis_raster", "PostGIS Raster", Geo, "Raster data types and analysis for PostGIS.", "https://postgis.net/docs/RT_reference.html"),
        new("postgis_topology", "PostGIS Topology", Geo, "Topological models: faces, edges and nodes.", "https://postgis.net/docs/Topology.html"),
        new("pgrouting", "pgRouting", Geo, "Shortest-path and routing algorithms over PostGIS networks.", "https://pgrouting.org"),
        new("h3", "H3", Geo, "Uber's hexagonal hierarchical spatial index.", "https://github.com/zachasme/h3-pg"),
        new("earthdistance", "earthdistance", Geo, "Great-circle distances on the surface of the Earth.", Core("earthdistance")),
        new("cube", "cube", Geo, "Multidimensional cube type; required by earthdistance.", Core("cube")),

        new("timescaledb", "TimescaleDB", Time, "Hypertables, automatic time partitioning, compression and continuous aggregates.", "https://docs.timescale.com", "timescaledb", true),
        new("citus", "Citus", Time, "Distributed tables and columnar storage across worker nodes.", "https://docs.citusdata.com", "citus", true),
        new("pg_partman", "pg_partman", Time, "Creates and maintains time and serial based partition sets.", "https://github.com/pgpartman/pg_partman", "partman"),
        new("pg_ivm", "pg_ivm", Time, "Incrementally maintained materialized views.", "https://github.com/sraoss/pg_ivm"),

        new("pg_stat_statements", "pg_stat_statements", Monitor, "Planning and execution statistics for every normalized statement.", Core("pgstatstatements"), "statements", true),
        new("pg_buffercache", "pg_buffercache", Monitor, "Shows which relations occupy the shared buffer cache right now.", Core("pgbuffercache"), "buffercache"),
        new("pg_stat_monitor", "pg_stat_monitor", Monitor, "Query performance monitoring with time buckets and histograms.", "https://docs.percona.com/pg-stat-monitor/", "", true),
        new("pg_wait_sampling", "pg_wait_sampling", Monitor, "Samples wait events to show where sessions spend their time.", "https://github.com/postgrespro/pg_wait_sampling", "", true),
        new("pg_qualstats", "pg_qualstats", Monitor, "Statistics on predicates in WHERE and JOIN clauses.", "https://github.com/powa-team/pg_qualstats", "", true),
        new("pgstattuple", "pgstattuple", Monitor, "Tuple-level statistics: dead rows, free space and index bloat.", Core("pgstattuple")),
        new("pg_visibility", "pg_visibility", Monitor, "Examines the visibility map and page-level visibility.", Core("pgvisibility")),
        new("pg_freespacemap", "pg_freespacemap", Monitor, "Examines the free space map.", Core("pgfreespacemap")),
        new("pgrowlocks", "pgrowlocks", Monitor, "Shows row-level locking information for a table.", Core("pgrowlocks")),
        new("pageinspect", "pageinspect", Monitor, "Low-level inspection of database pages.", Core("pageinspect")),
        new("pg_walinspect", "pg_walinspect", Monitor, "Inspects the contents of the write-ahead log.", Core("pgwalinspect")),
        new("hypopg", "HypoPG", Monitor, "Hypothetical indexes: test whether an index would be used without building it.", "https://hypopg.readthedocs.io"),

        new("pg_trgm", "pg_trgm", Text, "Trigram similarity and index support for fuzzy matching and fast LIKE/ILIKE.", Core("pgtrgm"), "trgm"),
        new("fuzzystrmatch", "fuzzystrmatch", Text, "Levenshtein, Soundex, Metaphone and Daitch-Mokotoff string distance.", Core("fuzzystrmatch")),
        new("unaccent", "unaccent", Text, "Text search dictionary that removes accents.", Core("unaccent")),
        new("pg_bigm", "pg_bigm", Text, "Bigram full-text search, suited to CJK text.", "https://github.com/pgbigm/pg_bigm"),
        new("dict_int", "dict_int", Text, "Text search dictionary template for integers.", Core("dict-int")),
        new("dict_xsyn", "dict_xsyn", Text, "Text search dictionary template for extended synonyms.", Core("dict-xsyn")),

        new("hstore", "hstore", Types, "Key/value pairs stored in a single column.", Core("hstore")),
        new("citext", "citext", Types, "Case-insensitive character string type.", Core("citext")),
        new("ltree", "ltree", Types, "Hierarchical tree-like labels with path queries.", Core("ltree")),
        new("uuid-ossp", "uuid-ossp", Types, "Generators for standard UUID versions.", Core("uuid-ossp")),
        new("intarray", "intarray", Types, "Functions, operators and index support for integer arrays.", Core("intarray")),
        new("isn", "isn", Types, "International product numbering types: ISBN, EAN13, UPC and others.", Core("isn")),
        new("seg", "seg", Types, "Line segments and floating-point intervals.", Core("seg")),
        new("lo", "lo", Types, "Large object maintenance.", Core("lo")),
        new("tablefunc", "tablefunc", Types, "Functions that return tables, including crosstab.", Core("tablefunc")),
        new("pg_jsonschema", "pg_jsonschema", Types, "JSON Schema validation for json and jsonb.", "https://github.com/supabase/pg_jsonschema"),

        new("btree_gin", "btree_gin", Indexes, "GIN operator classes with B-tree behaviour, for multicolumn GIN indexes.", Core("btree-gin")),
        new("btree_gist", "btree_gist", Indexes, "GiST operator classes with B-tree behaviour; enables exclusion constraints.", Core("btree-gist")),
        new("bloom", "bloom", Indexes, "Bloom filter index access method.", Core("bloom")),
        new("amcheck", "amcheck", Indexes, "Verifies the logical consistency of relations and indexes.", Core("amcheck")),

        new("postgres_fdw", "postgres_fdw", Foreign, "Query tables that live in another PostgreSQL server.", Core("postgres-fdw"), "fdw"),
        new("file_fdw", "file_fdw", Foreign, "Read server-side files such as CSV as foreign tables.", Core("file-fdw"), "fdw"),
        new("dblink", "dblink", Foreign, "Connect to other PostgreSQL databases from within a session.", Core("dblink")),
        new("mysql_fdw", "mysql_fdw", Foreign, "Foreign data wrapper for MySQL.", "https://github.com/EnterpriseDB/mysql_fdw", "fdw"),
        new("tds_fdw", "tds_fdw", Foreign, "Foreign data wrapper for SQL Server and Sybase.", "https://github.com/tds-fdw/tds_fdw", "fdw"),
        new("oracle_fdw", "oracle_fdw", Foreign, "Foreign data wrapper for Oracle.", "https://github.com/laurenz/oracle_fdw", "fdw"),
        new("pg_duckdb", "pg_duckdb", Foreign, "Embeds DuckDB for analytics over Postgres, Parquet and object storage.", "https://github.com/duckdb/pg_duckdb", "", true),

        new("pgcrypto", "pgcrypto", Security, "Cryptographic functions: hashing, HMAC, PGP and raw encryption.", Core("pgcrypto")),
        new("pgaudit", "pgAudit", Security, "Detailed session and object audit logging.", "https://github.com/pgaudit/pgaudit", "", true),
        new("sslinfo", "sslinfo", Security, "Information about the SSL certificate of the current client.", Core("sslinfo")),
        new("pgjwt", "pgjwt", Security, "JSON Web Token signing and verification.", "https://github.com/michelp/pgjwt"),
        new("pgsodium", "pgsodium", Security, "Modern cryptography with libsodium.", "https://github.com/michelp/pgsodium"),
        new("anon", "PostgreSQL Anonymizer", Security, "Masks or replaces personally identifiable information.", "https://postgresql-anonymizer.readthedocs.io"),

        new("pg_repack", "pg_repack", Admin, "Removes bloat from tables and indexes with minimal locking.", "https://reorg.github.io/pg_repack/"),
        new("pg_squeeze", "pg_squeeze", Admin, "Removes unused space from tables without long exclusive locks.", "https://github.com/cybertec-postgresql/pg_squeeze", "", true),
        new("pg_prewarm", "pg_prewarm", Admin, "Loads relation data into the buffer cache.", Core("pgprewarm")),
        new("pg_hint_plan", "pg_hint_plan", Admin, "Steers the planner with hints in SQL comments.", "https://github.com/ossc-db/pg_hint_plan"),
        new("pg_surgery", "pg_surgery", Admin, "Last-resort repair of corrupted relations.", Core("pgsurgery")),
        new("pg_tle", "pg_tle", Admin, "Trusted Language Extensions: build extensions without file-system access.", "https://github.com/aws/pg_tle"),
        new("tcn", "tcn", Admin, "Trigger function that notifies listeners of table changes.", Core("tcn")),
        new("moddatetime", "moddatetime", Admin, "Trigger function that tracks last modification time.", Core("contrib-spi")),

        new("pg_cron", "pg_cron", Jobs, "Runs SQL on a cron schedule inside the database.", "https://github.com/citusdata/pg_cron", "cron", true),
        new("pgmq", "PGMQ", Jobs, "Lightweight message queue with visibility timeouts and archives.", "https://github.com/pgmq/pgmq", "pgmq"),
        new("pg_net", "pg_net", Jobs, "Asynchronous HTTP requests from SQL.", "https://github.com/supabase/pg_net", "", true),
        new("pg_background", "pg_background", Jobs, "Runs SQL in background worker processes.", "https://github.com/vibhorkum/pg_background"),

        new("plpgsql", "PL/pgSQL", Languages, "The built-in procedural language.", Core("plpgsql")),
        new("plpython3u", "PL/Python", Languages, "Write functions in Python 3 (untrusted).", Core("plpython")),
        new("plperl", "PL/Perl", Languages, "Write functions in Perl.", Core("plperl")),
        new("pltcl", "PL/Tcl", Languages, "Write functions in Tcl.", Core("pltcl")),
        new("plv8", "PL/v8", Languages, "Write functions in JavaScript.", "https://plv8.github.io"),
        new("pldbgapi", "pldebugger", Languages, "Debugger API for PL/pgSQL.", "https://github.com/EnterpriseDB/pldebugger"),

        new("age", "Apache AGE", Analytics, "Graph database with openCypher queries on Postgres.", "https://age.apache.org", "age"),
        new("pg_graphql", "pg_graphql", Analytics, "GraphQL resolver that reflects the SQL schema.", "https://github.com/supabase/pg_graphql"),
        new("tsm_system_rows", "tsm_system_rows", Analytics, "TABLESAMPLE method that returns a fixed number of rows.", Core("tsm-system-rows")),
        new("tsm_system_time", "tsm_system_time", Analytics, "TABLESAMPLE method limited by time.", Core("tsm-system-time")),
        new("hll", "HyperLogLog", Analytics, "Approximate distinct counts in a compact data type.", "https://github.com/citusdata/postgresql-hll"),
        new("timescaledb_toolkit", "Timescale Toolkit", Analytics, "Hyperfunctions for time-series analysis.", "https://github.com/timescale/timescaledb-toolkit"),
    ];

    /// <summary>
    /// The dedicated pages an installed extension adds to the navigation, in the order they are listed there.
    /// The key is the guide's <see cref="ExtensionGuide.Insight"/>; several extensions may share one page
    /// (every foreign data wrapper opens "Foreign data").
    /// </summary>
    public static readonly ExtensionPageInfo[] Pages =
    [
        new("vector", "Vector lab", "Map a stored vector column, find a row's nearest neighbours and check coverage."),
        new("statements", "Query stats", "The statements that cost the most execution time, with calls, mean time and cache hits."),
        new("postgis", "PostGIS", "Geometry and geography columns with their type and SRID, and a plotted sample."),
        new("timescaledb", "Hypertables", "Hypertables by chunk count, with compression and background jobs."),
        new("cron", "Scheduled jobs", "Each pg_cron job with its most recent runs, so failures stand out."),
        new("trgm", "Text similarity", "Compare two strings and see their shared trigrams and similarity scores."),
        new("buffercache", "Buffer cache", "Which relations occupy the shared buffer cache right now."),
        new("fdw", "Foreign data", "Foreign servers and foreign tables defined through the installed wrappers."),
        new("partman", "Partitions", "The partition sets pg_partman creates and retires."),
        new("pgmq", "Queues", "Queue depth and message age for every PGMQ queue."),
        new("citus", "Citus", "Cluster nodes and the distributed and reference tables Citus manages."),
        new("age", "Graphs", "Apache AGE graphs with their vertex and edge labels.")
    ];

    /// <summary>Key of the page an extension opens, or null when it has none. pgvectorscale builds on pgvector, so it opens the same lab.</summary>
    public static string? PageKey(ExtensionState state) =>
        state.Name == "vectorscale" ? "vector" : state.Guide?.Insight is { Length: > 0 } key ? key : null;

    /// <summary>The pages that apply to a database, each with the installed extensions behind it.</summary>
    public static List<(ExtensionPageInfo Page, List<ExtensionState> Extensions)> PagesFor(IEnumerable<ExtensionState> states)
    {
        var installed = states.Where(s => s.Installed).ToList();
        return Pages.Select(page => (Page: page, Extensions: installed.Where(s => PageKey(s) == page.Key).ToList()))
            .Where(entry => entry.Extensions.Count > 0).ToList();
    }

    static readonly Dictionary<string, ExtensionGuide> ByName = Guides.ToDictionary(g => g.Name, StringComparer.Ordinal);
    public static ExtensionGuide? Guide(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Prefixes of the configuration parameters an extension registers. Most use their own name.</summary>
    public static string[] SettingPrefixes(string name) => name switch
    {
        "vector" => ["hnsw", "ivfflat"],
        "pg_cron" => ["cron"],
        "postgis" => ["postgis"],
        "pg_partman" => ["pg_partman_bgw"],
        _ => [name]
    };

    /// <summary>Merges the server's list with the guide so well-known extensions the server lacks are still explained.</summary>
    public static List<ExtensionState> Merge(IEnumerable<ExtensionState> server)
    {
        var merged = server.ToDictionary(e => e.Name, StringComparer.Ordinal);
        foreach (var guide in Guides)
            if (!merged.ContainsKey(guide.Name)) merged[guide.Name] = new(guide.Name, null, null, null, "", guide);
        // Installed first, then installable, then the rest. Within each group the guide's own order leads
        // (pgvector, PostGIS, …) so the most widely used extensions are not buried alphabetically.
        int Rank(ExtensionState e) => e.Guide == null ? int.MaxValue : Array.IndexOf(Guides, e.Guide);
        return merged.Values
            .OrderByDescending(e => e.Installed).ThenByDescending(e => e.Available)
            .ThenBy(Rank).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
