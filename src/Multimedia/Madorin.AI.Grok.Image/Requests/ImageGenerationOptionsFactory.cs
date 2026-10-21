using System.Text.Json.Nodes;
using Madorin.AI.Grok.Image.Metadata;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Image.Requests;

internal sealed class ImageGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> AllowedOptions = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir",
        "aspect-ratio", "num-images", "timeout", "try-run", "verbose", "no-wait", "output-format",
        "size", "width", "height", "quality", "seed", "style", "negative-prompt", "guidance-scale", "steps"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);

    public async Task<ImageGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = ImageAttachments.Extract(args);
        var direct = Parse(extracted.Remaining, AllowedOptions, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout)) { invocation.SetTimeout(ParsePositiveInt(directTimeout, 300, "timeout")); }

        var fileName = direct.GetValueOrDefault("request-file");
        ImageRequestFile? file = fileName is null ? null : await new GrokImageRequestReader(paths, invocation).ReadAsync(
            fileName, invocation.CancellationToken, direct.TryGetValue("try-run", out var directTryRun) ? directTryRun == "true" : null);
        var fileId = file is null ? null : ImageParameterValues.Text(file.Content, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId)
        { throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。"); }

        invocation.ResponseId = id;
        if (file is not null)
        {
            ImageAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory);
            if (file.Content["execution"] is JsonObject controls && controls.ContainsKey("output_dir"))
            { paths.Resolve(ImageParameterValues.Text(controls, "output_dir")!, file.BaseDirectory); }
        }
        ImageAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = ImageInputEcho.Build(direct, file);
        input["request_id"] = S(id);
        if (extracted.Attachments is not null) { input["attachments"] = extracted.Attachments; }
        var parameters = input["parameters"]!.AsObject();
        var execution = input["execution"]!.AsObject();
        invocation.TryRun = ImageParameterValues.Boolean(execution, "try_run");
        var timeout = ImageParameterValues.Integer(execution, "timeout", 300);
        invocation.SetTimeout(timeout);
        var tryRun = invocation.TryRun;
        var verbose = ImageParameterValues.Boolean(execution, "verbose");
        if (ImageParameterValues.Boolean(execution, "no_wait")) { throw new SkillException("invalid_parameter", "Grok 图片生成当前不支持 no_wait=true。"); }
        if (ImageParameterValues.Text(execution, "output_format", "json") != "json") { throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。"); }

        var task = ImageParameterValues.Text(input, "task", "text-to-image")!;
        var model = ImageParameterValues.Text(input, "model", GrokImageMetadata.DefaultModel)!;
        invocation.Model = model;
        var prompt = ImageParameterValues.Text(input, "prompt");
        var aspectRatio = ImageParameterValues.Text(parameters, "aspect_ratio", "auto")!;
        var count = ImageParameterValues.Integer(parameters, "num_images", 1);
        Validate(task, model, prompt, aspectRatio, count);

        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? GrokImageMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback) ||
            endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.UserInfo.Length > 0)
        { throw new SkillException("invalid_parameter", "必须提供 api-key 及不含用户信息、查询参数或片段的 HTTPS API 地址；仅本机回环地址允许 HTTP。"); }

        var output = ImageParameterValues.Text(execution, "output_dir");
        if (!tryRun && string.IsNullOrWhiteSpace(output)) { throw new SkillException("invalid_parameter", "正式生成必须指定 output-dir。"); }
        var resolvedOutput = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);

        foreach (var name in parameters.Select(pair => pair.Key).ToArray())
        {
            if (name is not ("aspect_ratio" or "num_images")) { Ignore("/parameters/" + EscapePointer(name), "unsupported_parameter"); parameters.Remove(name); }
        }
        if (input["provider_parameters"] is JsonObject providers)
        {
            foreach (var branch in providers.ToArray()) { Ignore("/provider_parameters/" + EscapePointer(branch.Key), branch.Key == "grok" ? "unsupported_parameter" : "other_provider"); providers.Remove(branch.Key); }
            if (providers.Count == 0) { input.Remove("provider_parameters"); }
        }
        if (input["attachments"] is JsonArray attachments)
        {
            for (var index = 0; index < attachments.Count; index++) { Ignore("/attachments/" + index, "unsupported_attachment_role"); }
            input["attachments"] = new JsonArray();
        }

        invocation.ThrowIfCancellationRequested();
        return new ImageGenerationOptions(id!, task, model, prompt!, apiKey, endpoint, resolvedOutput, aspectRatio, "auto", count, "jpeg", timeout, tryRun, verbose, input);

        void Ignore(string pointer, string reason) => invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S(pointer)), ("reason", S(reason))));
    }

    private static void Validate(string task, string model, string? prompt, string aspectRatio, int count)
    {
        if (task != "text-to-image" || string.IsNullOrWhiteSpace(prompt) || !ModelIds.IsValid(model) ||
            !GrokImageMetadata.SupportedAspectRatios.Contains(aspectRatio, StringComparer.Ordinal) || count is < 1 or > 10)
        { throw new SkillException("invalid_parameter", "任务、模型、宽高比、提示词或生成数量不合法。"); }
    }
}
