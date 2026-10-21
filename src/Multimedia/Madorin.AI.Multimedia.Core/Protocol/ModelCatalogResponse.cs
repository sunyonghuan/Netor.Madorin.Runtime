using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;

namespace Madorin.AI.Multimedia.Core.Protocol;

/// <summary>规范化模型目录数据；只保留服务返回的标识，不推断模型任务能力。</summary>
public static class ModelCatalogResponse
{
    /// <summary>校验服务目录并构造关联本次调用的响应，不复制原始响应或敏感字段。</summary>
    public static JsonObject Parse(string body, string provider, SkillInvocation invocation)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || JsonProtocol.HasDuplicateProperties(root) ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new SkillException("provider_error", "模型目录缺少 data 数组。");
            }

            var models = new JsonArray();
            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                invocation.ThrowIfCancellationRequested();
                var modelId = JsonProtocol.GetString(item, "id");
                if (!ModelIds.IsValid(modelId) || !modelIds.Add(modelId!))
                {
                    throw new SkillException("provider_error", "模型目录包含无效或重复的 ID。");
                }

                // 模型目录未提供可验证的任务能力时保持未知，不按名称推断。
                models.Add((JsonNode)JsonProtocol.Obj(("id", JsonProtocol.S(modelId)), ("name", JsonProtocol.S(modelId)), ("tasks", null)));
            }

            return JsonProtocol.Obj(("schema_version", JsonProtocol.S(SkillProtocol.ContractVersion)),
                ("response_id", JsonProtocol.S(invocation.ResponseId)), ("provider", JsonProtocol.S(provider)),
                ("model_source", JsonProtocol.S("api")), ("models", models));
        }
        catch (JsonException)
        {
            throw new SkillException("provider_error", "模型目录不是有效 JSON。");
        }
    }
}
