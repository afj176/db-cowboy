using System.Text.Json;

namespace DbCowboy;

public static class EmbeddingMetadata
{
    public static string Pretty(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        void Expand(System.Text.Json.Nodes.JsonNode? current, int depth)
        {
            if (depth > 3 || current is not System.Text.Json.Nodes.JsonObject obj) return;
            foreach (var name in obj.Select(p => p.Key).ToArray())
            {
                if (obj[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && text.TrimStart().StartsWith('{'))
                {
                    try { obj[name] = System.Text.Json.Nodes.JsonNode.Parse(text); } catch (JsonException) { }
                }
                Expand(obj[name], depth + 1);
            }
        }
        Expand(node, 0);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }
    public static Dictionary<string, string> Flatten(string json)
    {
        var fields = new Dictionary<string, string>(); using var document = JsonDocument.Parse(json);
        void Walk(JsonElement element, string prefix, int depth)
        {
            if (depth > 3) return;
            if (element.ValueKind == JsonValueKind.Object) { foreach (var property in element.EnumerateObject()) Walk(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, depth + 1); return; }
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value && value.TrimStart().StartsWith('{'))
            {
                try { using var nested = JsonDocument.Parse(value); Walk(nested.RootElement, prefix, depth + 1); return; } catch (JsonException) { }
            }
            if (element.ValueKind != JsonValueKind.Array) fields[prefix] = element.ValueKind == JsonValueKind.Null ? "NULL" : element.ToString();
        }
        Walk(document.RootElement, "", 0); return fields;
    }
}
