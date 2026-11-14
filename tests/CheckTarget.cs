using Npgsql;

namespace DbCowboy;

/// <summary>
/// The database the read-only VectorChecks and ConnectionChecks read: a local, disposable fixture named by
/// DBCOWBOY_CHECK_CONNECTION as an Npgsql connection string, for example
/// <c>Host=127.0.0.1;Port=55439;Database=cowboy_vectors;Username=postgres</c>.
/// </summary>
public static class CheckTarget
{
    public const string Variable = "DBCOWBOY_CHECK_CONNECTION";

    public static (Profile Profile, string Password) Resolve()
    {
        var text = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(text)) throw new Exception($"Set {Variable} to the connection string of a local fixture database (see tests/DbCowboy.VectorChecks/fixture.sql).");
        var builder = new NpgsqlConnectionStringBuilder(text);
        // The checks only read, but they are still meant for a fixture on this machine, never a shared server.
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1")) throw new Exception(Variable + " must point at a local fixture (localhost, 127.0.0.1 or ::1).");
        var fixture = new Profile { Name = "Check fixture", Host = builder.Host, Port = builder.Port, Database = builder.Database ?? "postgres", Username = builder.Username ?? "postgres", Ssl = builder.SslMode.ToString() };
        return (fixture, builder.Password ?? "");
    }
}
