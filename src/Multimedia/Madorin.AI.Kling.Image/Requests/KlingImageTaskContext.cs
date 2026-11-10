using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Image.Metadata;
using static Madorin.AI.Multimedia.Core.Protocol.ParameterValues;

namespace Madorin.AI.Kling.Image.Requests;

internal static class KlingImageTaskContext
{
    public static async Task<JsonObject> ReadAsync(string file, string taskId, WorkspacePaths paths, CancellationToken cancellationToken)
    {
        using var document = await new RequestFileReader(paths).ReadAsync(file, cancellationToken);
        var value = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        var allowed = new HashSet<string>(["schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context"], StringComparer.Ordinal);
        if (value.Count != allowed.Count || value.Any(pair => !allowed.Contains(pair.Key)) ||
            Text(value, "schema_version") != SkillProtocol.ContractVersion ||
            Text(value, "skill_id") != KlingImageMetadata.SkillId || Text(value, "provider") != "kling" ||
            Text(value, "task_id") != taskId || Text(value, "task") != "text-to-image" ||
            !ModelIds.IsValid(Text(value, "model")) || !ValidIndices(value["output_indices"] as JsonArray) ||
            value["provider_context"] is not JsonObject provider || provider.Count != 0)
        {
            throw new SkillException("invalid_parameter", "任务上下文版本、身份、模型、任务或槽位不合法。");
        }

        return value;
    }

    private static bool ValidIndices(JsonArray? indices)
    {
        if (indices is null || indices.Count is < 1 or > 9) return false;
        for (var index = 0; index < indices.Count; index++)
        {
            if (indices[index] is not JsonValue value || !value.TryGetValue<int>(out var slot) || slot != index + 1) return false;
        }

        return true;
    }
}
