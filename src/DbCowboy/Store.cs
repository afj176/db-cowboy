using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
namespace DbCowboy;

public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid(); public string Name { get; set; } = "Local Postgres"; public string Host { get; set; } = "localhost"; public int Port { get; set; } = 5432; public string Database { get; set; } = "postgres"; public string Username { get; set; } = "postgres"; public string Ssl { get; set; } = "VerifyFull"; public string RootCertificate { get; set; } = ""; public string Secret { get; set; } = ""; public bool ReadOnly { get; set; } = true; public bool Production { get; set; } public string Avatar { get; set; } = "Plain";
    public string ConnectionString(string password) => new NpgsqlConnectionStringBuilder { Host = Host, Port = Port, Database = Database, Username = Username, Password = password, SslMode = Enum.Parse<SslMode>(Ssl), RootCertificate = string.IsNullOrWhiteSpace(RootCertificate) ? null : RootCertificate, Timeout = 10, CommandTimeout = 60, ApplicationName = "DB Cowboy", Pooling = true, MaxPoolSize = 4, IncludeErrorDetail = false }.ConnectionString;
    public override string ToString() => Name;
    public bool Favorite { get; set; }
    public DateTimeOffset? LastConnected { get; set; }
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

