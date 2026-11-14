using Npgsql;

namespace DbCowboy;

/// <summary>What a pasted connection string says. A null member was not present in the text.</summary>
public sealed record ParsedConnection(string? Host = null, int? Port = null, string? Database = null, string? Username = null, string? Password = null,
    string? Ssl = null, string? RootCertificate = null, string? ClientCertificate = null, string? ClientKey = null)
{
    /// <summary>The parts that were read, for telling the user what was filled in.</summary>
    public IEnumerable<string> Parts()
    {
        if (Host != null) yield return "host"; if (Port != null) yield return "port"; if (Database != null) yield return "database";
        if (Username != null) yield return "user"; if (Password != null) yield return "password"; if (Ssl != null) yield return "TLS mode";
        if (RootCertificate != null || ClientCertificate != null || ClientKey != null) yield return "certificates";
    }
}

/// <summary>
/// Reads the three forms a Postgres connection is handed around in: a <c>postgres://</c> URL, a libpq
/// <c>host=… dbname=…</c> string and an Npgsql <c>Host=…;Database=…</c> string.
/// </summary>
public static class ConnectionUrl
{
    public static ParsedConnection? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        try
        {
            if (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return Url(text);
            if (!text.Contains('=')) return null;
            return text.Contains(';') ? Npgsql(text) : Libpq(text);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or KeyNotFoundException or InvalidCastException) { return null; }
    }

    static ParsedConnection? Url(string text)
    {
        // One host only: a multi-host URL names a cluster, which a single connection cannot represent.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        string[] user = uri.UserInfo.Split(':', 2);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0].ToLowerInvariant()).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last()[1]));
        string database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        return new(uri.Host.Trim('[', ']'), uri.IsDefaultPort || uri.Port < 0 ? null : uri.Port, database.Length > 0 ? database : null,
            user[0].Length > 0 ? Uri.UnescapeDataString(user[0]) : null, user.Length > 1 ? Uri.UnescapeDataString(user[1]) : null,
            Mode(query.GetValueOrDefault("sslmode")), query.GetValueOrDefault("sslrootcert"), query.GetValueOrDefault("sslcert"), query.GetValueOrDefault("sslkey"));
    }

    static ParsedConnection? Libpq(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"(\w+)\s*=\s*('(?:[^'\\]|\\.)*'|\S+)"))
        {
            string value = match.Groups[2].Value;
            if (value.StartsWith('\'')) value = value[1..^1].Replace("\\'", "'").Replace("\\\\", "\\");
            values[match.Groups[1].Value] = value;
        }
        if (!values.ContainsKey("host") && !values.ContainsKey("dbname")) return null;
        return new(values.GetValueOrDefault("host"), values.TryGetValue("port", out var port) ? int.Parse(port) : null, values.GetValueOrDefault("dbname"),
            values.GetValueOrDefault("user"), values.GetValueOrDefault("password"), Mode(values.GetValueOrDefault("sslmode")),
            values.GetValueOrDefault("sslrootcert"), values.GetValueOrDefault("sslcert"), values.GetValueOrDefault("sslkey"));
    }

    static ParsedConnection? Npgsql(string text)
    {
        var builder = new NpgsqlConnectionStringBuilder(text);
        if (string.IsNullOrWhiteSpace(builder.Host)) return null;
        // The builder reports defaults for keys the text never named, so presence is read from the text itself.
        var named = text.Split(';').Select(pair => pair.Split('=')[0].Trim().Replace(" ", "").ToLowerInvariant()).ToHashSet();
        return new(builder.Host, named.Contains("port") ? builder.Port : null, builder.Database, builder.Username, builder.Password, named.Contains("sslmode") ? builder.SslMode.ToString() : null,
            builder.RootCertificate, builder.SslCertificate, builder.SslKey);
    }

    /// <summary>libpq's sslmode names as the profile's TLS mode. "allow" has no equivalent and reads as Prefer.</summary>
    static string? Mode(string? sslmode) => sslmode?.ToLowerInvariant() switch
    {
        "disable" => "Disable", "allow" or "prefer" => "Prefer", "require" => "Require", "verify-ca" => "VerifyCA", "verify-full" => "VerifyFull", _ => null
    };
}
