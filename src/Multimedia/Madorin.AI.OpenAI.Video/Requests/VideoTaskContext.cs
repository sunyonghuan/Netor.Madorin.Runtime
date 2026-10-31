using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.ParameterValues;

namespace Madorin.AI.OpenAI.Video.Requests;

internal static class VideoTaskContext
{
    public static JsonObject Create(string id, string task, string model) => Obj(("schema_version", S("1.0")),
        ("skill_id", S("skill-openai-video")), ("provider", S("openai")), ("task_id", S(id)), ("task", S(task)),
        ("model", S(model)), ("output_indices", new JsonArray(N(1))), ("provider_context", new JsonObject()));

    public static bool IsValidTaskId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 512 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static async Task<JsonObject> ReadAsync(string file, string taskId, WorkspacePaths paths, CancellationToken cancellationToken)
    {
        using var document = await new RequestFileReader(paths).ReadAsync(file, cancellationToken);
        var value = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        var allowed = new HashSet<string>(["schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context"], StringComparer.Ordinal);
        if (value.Count != allowed.Count || value.Any(pair => !allowed.Contains(pair.Key)) ||
            Text(value, "schema_version") != "1.0" || Text(value, "skill_id") != "skill-openai-video" || Text(value, "provider") != "openai" ||
            Text(value, "task_id") != taskId || Text(value, "task") is not ("text-to-video" or "image-to-video") ||
            !ModelIds.IsValid(Text(value, "model")) || value["output_indices"] is not JsonArray indices || indices.Count != 1 ||
            indices[0] is not JsonValue index || !index.TryGetValue<int>(out var slot) || slot != 1 ||
            value["provider_context"] is not JsonObject provider || provider.Count != 0)
        {
            throw new SkillException("invalid_parameter", "任务上下文版本、身份、模型、任务或槽位不合法。");
        }
        return value;
    }
}
