using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.RequestIds;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.OpenAI.Image.Requests;

internal sealed class OpenAiImageRequestReader(WorkspacePaths paths, SkillInvocation invocation)
{
    public async Task<ImageRequestFile> ReadAsync(string file, CancellationToken cancellationToken, bool? tryRunOverride = null)
    {
        var path = paths.Resolve(file);
        using var document = await new RequestFileReader(paths).ReadAsync(file, cancellationToken);
        var content = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        if (content["request_id"] is JsonValue id && id.TryGetValue<string>(out var earlyId) && IsValidRequestId(earlyId))
        {
            invocation.ResponseId ??= earlyId;
        }

        if (tryRunOverride is null && content["execution"] is JsonObject controls && controls["try_run"] is JsonValue mode && mode.TryGetValue<bool>(out var tryRun))
        {
            invocation.TryRun = tryRun;
        }

        invocation.ThrowIfCancellationRequested();
        if (ContainsCredentialField(document.RootElement))
        {
            throw new SkillException("invalid_parameter", "请求文件不得包含认证字段。");
        }

        var allowed = new HashSet<string>(["schema_version", "request_id", "task", "model", "prompt", "attachments", "parameters", "provider_parameters", "execution"], StringComparer.Ordinal);
        foreach (var property in content)
        {
            if (!allowed.Contains(property.Key))
            {
                throw new SkillException("invalid_parameter", "请求文件包含未知根字段。");
            }
        }
        if (content["schema_version"] is not JsonValue schema || !schema.TryGetValue<string>(out var version) || version != ContractVersion)
        {
            throw new SkillException("invalid_parameter", "请求版本或 task 不支持。");
        }

        foreach (var name in new[] { "parameters", "execution", "provider_parameters" })
        {
            if (content.ContainsKey(name) && content[name] is not JsonObject)
            {
                throw new SkillException("invalid_parameter", name + " 必须是对象。");
            }
        }

        if (content["provider_parameters"] is JsonObject providers && providers.Any(pair => pair.Value is not JsonObject))
        {
            throw new SkillException("invalid_parameter", "Provider 参数命名空间必须是对象。");
        }

        var allowedExecution = new HashSet<string>(["output_dir", "timeout", "try_run", "verbose", "no_wait", "output_format"], StringComparer.Ordinal);
        if (content["execution"] is JsonObject execution && execution.Any(pair => !allowedExecution.Contains(pair.Key)))
        {
            throw new SkillException("invalid_parameter", "execution 包含未知字段。");
        }

        if (content.ContainsKey("request_id") && (content["request_id"] is not JsonValue requestId || !requestId.TryGetValue<string>(out var requestIdText) || !IsValidRequestId(requestIdText)))
        {
            throw new SkillException("invalid_parameter", "request_id 格式无效。");
        }

        return new(content, Path.GetDirectoryName(path)!);
    }
}
