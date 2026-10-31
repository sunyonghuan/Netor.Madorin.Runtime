using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.Protocol;

public static class ParameterValues
{
    public static string? Text(JsonObject value, string name, string? fallback = null)
    {
        if (!value.ContainsKey(name)) { return fallback; }
        if (value[name] is JsonValue item && item.TryGetValue<string>(out var text)) { return text; }
        throw Invalid(name);
    }

    public static int PositiveNumber(JsonObject value, string name, int fallback)
    {
        if (!value.ContainsKey(name)) { return fallback; }
        var raw = value[name]?.ToJsonString();
        if (raw is not null && raw.Length > 0 && raw.All(character => character is >= '0' and <= '9') && int.TryParse(raw, out var number) && number > 0) { return number; }
        throw Invalid(name);
    }

    public static bool Boolean(JsonObject value, string name, bool fallback = false)
    {
        if (!value.ContainsKey(name)) { return fallback; }
        if (value[name] is JsonValue item && item.TryGetValue<bool>(out var flag)) { return flag; }
        throw Invalid(name);
    }

    private static SkillException Invalid(string name) => new("invalid_parameter", name + " 的类型或取值无效。");
}
