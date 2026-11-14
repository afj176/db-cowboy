using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
namespace DbCowboy;

public sealed record Profile : System.Text.Json.Serialization.IJsonOnDeserialized
{
    public Guid Id { get; init; } = Guid.NewGuid(); public string Name { get; set; } = "Local Postgres"; public string Host { get; set; } = "localhost"; public int Port { get; set; } = 5432; public string Database { get; set; } = "postgres"; public string Username { get; set; } = "postgres"; public string Ssl { get; set; } = "VerifyFull"; public string RootCertificate { get; set; } = ""; public string Secret { get; set; } = ""; public bool ReadOnly { get; set; } = true; public bool Production { get; set; } public string Identity { get; set; } = "Plain";
    /// <summary>The art a connection can wear; the first means none.</summary>
    public static readonly string[] Identities = ["Plain", "Rowdy", "Dash", "Dot"];
    // Earlier versions saved the identity under an earlier property name. Whatever a file holds that is not a
    // property of this record lands here while it is read; a leftover value that names an identity is adopted, and
    // nothing is kept, so the next save writes only the current names.
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Leftovers { get; set; }
    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        if (Identity == Identities[0] && Leftovers != null)
            foreach (var value in Leftovers.Values)
                if (value.ValueKind == JsonValueKind.String && Array.IndexOf(Identities, value.GetString()) > 0) { Identity = value.GetString()!; break; }
        Leftovers = null;
        // Profiles saved before tags existed carry only the production mark.
        if (Production && Tag.Length == 0) Tag = "Production";
    }
    /// <summary>
    /// The Npgsql connection string. <paramref name="via"/> is the local end of an SSH tunnel; the server's own
    /// name is still what TLS verifies (see <see cref="Db"/>).
    /// </summary>
    public string ConnectionString(string password, (string Host, int Port)? via = null) => new NpgsqlConnectionStringBuilder
    {
        Host = via?.Host ?? Host, Port = via?.Port ?? Port, Database = Database, Username = Username, Password = password, SslMode = Enum.Parse<SslMode>(Ssl),
        RootCertificate = string.IsNullOrWhiteSpace(RootCertificate) ? null : RootCertificate,
        SslCertificate = string.IsNullOrWhiteSpace(ClientCertificate) ? null : ClientCertificate,
        SslKey = string.IsNullOrWhiteSpace(ClientKey) ? null : ClientKey,
        Timeout = 10, CommandTimeout = 60, ApplicationName = "DB Cowboy", Pooling = true, MaxPoolSize = 4, IncludeErrorDetail = false
    }.ConnectionString;
    public override string ToString() => Name;
    public bool Favorite { get; set; }
    public DateTimeOffset? LastConnected { get; set; }

    /// <summary>What kind of database this is. "Production" keeps <see cref="Production"/> set; empty means untagged.</summary>
    public string Tag { get; set; } = "";
    public static readonly string[] Tags = ["", "Local", "Development", "Staging", "Production"];
    /// <summary>Never keep the password: ask for it on every connect.</summary>
    public bool AskPassword { get; set; }
    /// <summary>Client certificate and private key files (PEM) for servers that authenticate by certificate.</summary>
    public string ClientCertificate { get; set; } = ""; public string ClientKey { get; set; } = "";

    // SSH tunnel: the database is reached from the SSH server, not from this computer.
    public bool Ssh { get; set; } public string SshHost { get; set; } = ""; public int SshPort { get; set; } = 22; public string SshUser { get; set; } = "";
    public string SshKeyFile { get; set; } = "";
    /// <summary>The SSH password, or the private key's passphrase, protected for this Windows account.</summary>
    public string SshSecret { get; set; } = "";
    /// <summary>The SSH server's host key fingerprint, recorded on the first connection and required to match afterwards.</summary>
    public string SshHostKey { get; set; } = "";
    /// <summary>Everything about the tunnel that changes which server a session talks to.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string TunnelKey => Ssh ? $"{SshUser}@{SshHost}:{SshPort}|{SshKeyFile}|{SshSecret}" : "";
    [System.Text.Json.Serialization.JsonIgnore] public string Endpoint=>$"{Database} · {Host}:{Port} · {Username}";
}
public sealed record QueryLog(DateTime At, string Connection, string Sql, int Rows, long Milliseconds);

/// <summary>A profile file that exists but could not be read at startup. <paramref name="Backup"/> is empty when no copy could be made.</summary>
public sealed record UnreadableFile(string File, string Backup, string Reason);

public static class Store
{
    public const string ConnectionsFile = "connections.json", HistoryFile = "history.json";
    public static string Root => Environment.GetEnvironmentVariable("DBCOWBOY_DATA_HOME") ?? (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")) ? Path.Combine(AppContext.BaseDirectory, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbCowboy"));
    public static List<Profile> Profiles() => Read<List<Profile>>(ConnectionsFile) ?? [];
    public static T? Read<T>(string file) { var path = Path.Combine(Root, file); return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }

    // Files that could not be read this session. They are never written again until the app restarts,
    // so an empty or partial state can never replace what the user had.
    static readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsUnreadable(string file) => unreadable.Contains(file);

    /// <summary>
    /// Reads a profile file. When it exists but cannot be read, a timestamped copy is kept beside it, the file is
    /// left as it is and marked unreadable for this session, and the problem is added to <paramref name="problems"/>.
    /// </summary>
    public static T? Load<T>(string file, List<UnreadableFile> problems)
    {
        try { return Read<T>(file); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            unreadable.Add(file);
            var path = Path.Combine(Root, file);
            string backup;
            try { backup = KeepCopy(path); }
            catch (Exception) { backup = ""; }   // the original is still untouched
            problems.Add(new(file, backup, error.Message.ReplaceLineEndings(" ")));
            return default;
        }
    }

    /// <summary>Copies an unreadable file aside once; a later start with the same contents reuses that copy.</summary>
    static string KeepCopy(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var existing = Directory.EnumerateFiles(Root, Path.GetFileName(path) + ".unreadable-*.bak").FirstOrDefault(copy => File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes));
        if (existing != null) return Path.GetFileName(existing);
        string backup = $"{Path.GetFileName(path)}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(path, Path.Combine(Root, backup), false);
        return backup;
    }

    public static void Write<T>(string file, T value)
    {
        if (unreadable.Contains(file)) throw new InvalidOperationException($"{file} could not be read when DB Cowboy started, so it is left unchanged until the next start.");
        Directory.CreateDirectory(Root); var path = Path.Combine(Root, file); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true);
    }
    public static string Protect(string text) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string text) => string.IsNullOrEmpty(text) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
}

