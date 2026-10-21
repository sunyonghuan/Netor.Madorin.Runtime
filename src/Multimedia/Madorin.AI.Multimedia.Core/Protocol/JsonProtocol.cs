using System.Text.Json;
using System.Text.Json.Nodes;

namespace Madorin.AI.Multimedia.Core.Protocol;

public static class JsonProtocol
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool ContainsCredentialField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "api_key" or "api-key" or "api_url" or "api-url" or "authorization" or "credentials" or "authentication" || ContainsCredentialField(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsCredentialField(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static JsonObject Obj(params (string Name, JsonNode? Value)[] items)
    {
        var value = new JsonObject();
        foreach (var item in items)
        {
            value.Add(item.Name, item.Value);
        }

        return value;
    }

    public static JsonValue? S(string? value) => value is null ? null : JsonValue.Create(value);

    public static JsonValue N(long value) => JsonValue.Create(value)!;

    public static JsonValue B(bool value) => JsonValue.Create(value)!;

    public static JsonArray Strings(params string[] values) => new(values.Select(value => (JsonNode?)S(value)).ToArray());

    public static string? GetString(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? GetInt(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
