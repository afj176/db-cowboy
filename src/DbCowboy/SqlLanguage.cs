using System.Text;
using System.Text.RegularExpressions;

namespace DbCowboy;

public enum SqlTokenKind { Word, Keyword, String, Identifier, Comment, Number, Symbol }
public sealed record SqlToken(int Start, int Length, SqlTokenKind Kind, string Value);
public sealed record Completion(string Label, string Insert, string Kind) { public override string ToString() => $"{Label}   ·   {Kind}"; }

/// <summary>PostgreSQL-aware lexical services. Strings, nested comments and dollar bodies remain opaque.</summary>
public static class SqlLanguage
{
    public static readonly HashSet<string> Keywords = new(("SELECT FROM WHERE JOIN LEFT RIGHT INNER OUTER FULL CROSS ON AS WITH RECURSIVE INSERT INTO VALUES UPDATE SET DELETE RETURNING CREATE ALTER DROP TABLE VIEW INDEX UNIQUE PRIMARY KEY FOREIGN REFERENCES NOT NULL DEFAULT AND OR IS DISTINCT BETWEEN IN EXISTS CASE WHEN THEN ELSE END GROUP BY ORDER ASC DESC LIMIT OFFSET HAVING UNION ALL EXCEPT INTERSECT OVER PARTITION FILTER EXPLAIN ANALYZE FORMAT JSON TRUE FALSE BEGIN COMMIT ROLLBACK TRANSACTION READ ONLY SHOW EXTENSION VECTOR HALFVEC LATERAL MATERIALIZED CONCURRENTLY COUNT SUM AVG MIN MAX COALESCE CAST TEXT INTEGER BOOLEAN TIMESTAMP NOW ILIKE LIKE ANY ARRAY" ).Split(' '), StringComparer.OrdinalIgnoreCase);

    public static List<SqlToken> Tokens(string sql)
    {
        var tokens = new List<SqlToken>();
        for (int i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            int start = i; var kind = SqlTokenKind.Symbol;
            if (i + 1 < sql.Length && sql[i..(i + 2)] == "--") { i += 2; while (i < sql.Length && sql[i] is not '\r' and not '\n') i++; kind = SqlTokenKind.Comment; }
            else if (i + 1 < sql.Length && sql[i..(i + 2)] == "/*")
            {
                i += 2; int depth = 1;
                while (i < sql.Length && depth > 0) { if (i + 1 < sql.Length && sql[i..(i + 2)] == "/*") { depth++; i += 2; } else if (i + 1 < sql.Length && sql[i..(i + 2)] == "*/") { depth--; i += 2; } else i++; }
                kind = SqlTokenKind.Comment;
            }
            else if (sql[i] is '\'' or '"')
            {
                char quote = sql[i++]; bool escape = quote == '\'' && start > 0 && (sql[start - 1] is 'e' or 'E') && (start < 2 || !char.IsLetterOrDigit(sql[start - 2]));
                while (i < sql.Length) { if (escape && sql[i] == '\\') { i = Math.Min(sql.Length, i + 2); continue; } if (sql[i++] != quote) continue; if (i < sql.Length && sql[i] == quote) { i++; continue; } break; }
                kind = quote == '"' ? SqlTokenKind.Identifier : SqlTokenKind.String;
            }
            else if (sql[i] == '$' && Regex.Match(sql[i..], @"^\$(?:[a-zA-Z_][a-zA-Z_0-9]*)?\$") is { Success: true } dollar)
            {
                int end = sql.IndexOf(dollar.Value, i + dollar.Length, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + dollar.Length; kind = SqlTokenKind.String;
            }
            else if (char.IsLetter(sql[i]) || sql[i] == '_') { while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++; kind = Keywords.Contains(sql[start..i]) ? SqlTokenKind.Keyword : SqlTokenKind.Word; }
            else if (char.IsDigit(sql[i])) { i++; while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++; kind = SqlTokenKind.Number; }
            else i++;
            tokens.Add(new(start, i - start, kind, sql[start..i]));
        }
        return tokens;
    }

    public static string CurrentStatement(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length); int start = 0, end = sql.Length;
        int previousStart = 0, previousEnd = 0;
        foreach (var token in Tokens(sql).Where(t => t.Kind == SqlTokenKind.Symbol && t.Value == ";")) { if (token.Start + 1 < caret) { previousStart = start; previousEnd = token.Start + 1; start = token.Start + 1; } else { end = token.Start + 1; break; } }
        var statement = sql[start..end].Trim();
        if (Tokens(statement).All(t => t.Kind == SqlTokenKind.Comment)) return previousEnd > 0 ? sql[previousStart..previousEnd].Trim() : "";
        return statement;
    }

    public static string Format(string sql)
    {
        var tokens = Tokens(sql); var text = new StringBuilder(); int previousEnd = 0;
        var breaks = new HashSet<string>(new[] { "SELECT", "FROM", "WHERE", "HAVING", "LIMIT", "OFFSET", "RETURNING", "UNION", "JOIN", "SET", "VALUES" }, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            string gap = sql[previousEnd..t.Start];
            if (t.Kind == SqlTokenKind.Keyword && breaks.Contains(t.Value) && text.Length > 0 && text[^1] != '\n') text.AppendLine();
            else if (gap.Length > 0 && text.Length > 0 && !char.IsWhiteSpace(text[^1])) text.Append(' ');
            text.Append(t.Kind == SqlTokenKind.Keyword ? t.Value.ToUpperInvariant() : t.Value);
            if (t.Kind == SqlTokenKind.Comment && t.Value.StartsWith("--")) text.AppendLine();
            previousEnd = t.Start + t.Length;
        }
        return text.ToString().Trim();
    }

    public static IReadOnlyList<Completion> Complete(string prefix, IEnumerable<Completion> catalog) => catalog
        .Concat(Keywords.Select(k => new Completion(k, k, "SQL")))
        .Where(c => c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || c.Label.Contains("." + prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => !c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ThenBy(c => c.Kind == "SQL")
        .DistinctBy(c => c.Insert).Take(30).ToList();
}
