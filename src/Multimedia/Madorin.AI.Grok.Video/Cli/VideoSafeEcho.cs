using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Grok.Video.Cli;

internal static class VideoSafeEcho
{
    public static JsonObject Sanitize(JsonObject value) => (JsonObject)SanitizeNode(value)!;

    public static JsonArray UnicodeChecks(JsonObject input)
    {
        var checks = SkillResponses.UnicodeChecks(input);
        for (var index = checks.Count - 1; index >= 0; index--)
        {
            if (checks[index]?["display_value"]?.GetValue<string>() == "<redacted>")
            {
                checks.RemoveAt(index);
            }
        }
        return checks;
    }

    private static JsonNode? SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            var result = new JsonObject();
            foreach (var pair in value)
            {
                result[pair.Key] = pair.Key is "api_key" or "authorization" or "token" or "password"
                    ? JsonValue.Create("<redacted>") : SanitizeNode(pair.Value);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(SanitizeNode).ToArray());
        }
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && ContainsSensitiveUri(text))
        {
            return JsonValue.Create("<redacted>");
        }
        return node?.DeepClone();
    }

    private static bool ContainsSensitiveUri(string text)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0))
        {
            return true;
        }

        // 提示词中的嵌入链接同样可能带签名；遮蔽整段，避免码点回显泄漏。
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf("http", position, StringComparison.OrdinalIgnoreCase);
            if (start < 0) { break; }
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"' or '\''))
            {
                end++;
            }
            var candidate = text[start..end];
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var embedded) &&
                embedded.Scheme is "http" or "https" && (embedded.Query.Length > 0 || embedded.UserInfo.Length > 0))
            {
                return true;
            }
            position = end;
        }
        return false;
    }
}
