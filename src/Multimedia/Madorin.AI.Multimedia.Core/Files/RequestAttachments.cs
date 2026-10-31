using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Multimedia.Core.Files;

public static class RequestAttachments
{
    public static (string[] Remaining, JsonArray? Attachments) Extract(string[] args)
    {
        var remaining = new List<string>();
        JsonArray? attachments = null;
        bool? explicitObjects = null;
        for (var i = 0; i < args.Length; i++)
        {
            var parts = args[i].Split('=', 2);
            var role = parts[0] switch
            {
                "--attachment" => "object",
                "--reference-image" => "reference",
                "--image-url" => "source",
                "--first-frame-url" => "first_frame",
                "--last-frame-url" => "last_frame",
                _ => null
            };
            if (role is null)
            {
                remaining.Add(args[i]);
                // 非布尔选项的值保持原始 argv 边界。
                if (parts.Length == 1 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    remaining.Add(args[++i]);
                }
                continue;
            }

            var value = parts.Length == 2 ? parts[1] : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : throw Invalid();
            if (explicitObjects is not null && explicitObjects != (role == "object")) { throw Invalid(); }
            explicitObjects = role == "object";
            attachments ??= [];
            if (role == "object")
            {
                try
                {
                    using var document = JsonDocument.Parse(value);
                    if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement)) { throw Invalid(); }
                    attachments.Add(JsonNode.Parse(value));
                }
                catch (JsonException) { throw Invalid(); }
            }
            else
            {
                var source = Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile ? "uri" : "path";
                attachments.Add((JsonNode)Obj(("id", S($"attachment_{attachments.Count + 1:000}")), ("role", S(role)), ("source", Obj((source, S(value))))));
            }
        }
        return (remaining.ToArray(), attachments);
    }

    public static void Validate(JsonNode? node, bool present, WorkspacePaths paths, string? baseDirectory)
    {
        if (!present) { return; }
        if (node is not JsonArray attachments) { throw Invalid(); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in attachments)
        {
            if (item is not JsonObject attachment || attachment.Count != 3 ||
                attachment["id"] is not JsonValue id || !id.TryGetValue<string>(out var idText) || string.IsNullOrWhiteSpace(idText) || !ids.Add(idText) ||
                attachment["role"] is not JsonValue role || !role.TryGetValue<string>(out var roleText) || roleText is not ("reference" or "source" or "first_frame" or "last_frame") ||
                attachment["source"] is not JsonObject source || source.Count != 1)
            { throw Invalid(); }

            var entry = source.First();
            if (entry.Value is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) { throw Invalid(); }
            if (entry.Key == "path")
            {
                if (Directory.Exists(paths.Resolve(text, baseDirectory))) { throw Invalid(); }
            }
            else if (entry.Key != "uri" || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            { throw Invalid(); }
        }
    }

    private static SkillException Invalid() => new("invalid_parameter", "附件结构、ID、角色或引用路径无效，附件入口与简写不能混用。");
}
