using System.Text.Json.Nodes;
using Madorin.AI.Aliyun.Image.Metadata;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
namespace Madorin.AI.Aliyun.Image.Requests;
internal sealed class ImageGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> AllowedOptions = new(["request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir", "size", "num-images", "negative-prompt", "provider-aliyun-prompt-extend", "provider-aliyun-watermark", "provider-aliyun-seed", "timeout", "try-run", "verbose", "no-wait", "output-format"], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);
    public async Task<ImageGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = ImageAttachments.Extract(args); var direct = Parse(extracted.Remaining, AllowedOptions, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout)) invocation.SetTimeout(ParsePositiveInt(directTimeout, 300, "timeout"));
        var fileName = direct.GetValueOrDefault("request-file");
        ImageRequestFile? file = fileName is null ? null : await new AliyunImageRequestReader(paths, invocation).ReadAsync(fileName, invocation.CancellationToken, direct.TryGetValue("try-run", out var directTryRun) ? directTryRun == "true" : null);
        var fileId = file is null ? null : ImageParameterValues.Text(file.Content, "request_id"); var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId) throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。");
        invocation.ResponseId = id;
        if (file is not null) { ImageAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory); if (file.Content["execution"] is JsonObject controls && controls.ContainsKey("output_dir")) paths.Resolve(ImageParameterValues.Text(controls, "output_dir")!, file.BaseDirectory); }
        ImageAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = ImageInputEcho.Build(direct, file); input["request_id"] = S(id); if (extracted.Attachments is not null) input["attachments"] = extracted.Attachments;
        var parameters = input["parameters"]?.AsObject() ?? new JsonObject();
        input["parameters"] = parameters;
        MergeProviderParameters(input, parameters);
        var execution = input["execution"]!.AsObject();
        invocation.TryRun = ImageParameterValues.Boolean(execution, "try_run"); var timeout = ImageParameterValues.Integer(execution, "timeout", 300); invocation.SetTimeout(timeout);
        var tryRun = invocation.TryRun; var verbose = ImageParameterValues.Boolean(execution, "verbose");
        if (ImageParameterValues.Boolean(execution, "no_wait")) throw new SkillException("invalid_parameter", "当前同步 Wan 2.6 文生图不支持 no_wait=true。");
        if (ImageParameterValues.Text(execution, "output_format", "json") != "json") throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        var task = ImageParameterValues.Text(input, "task", "text-to-image")!; var model = ImageParameterValues.Text(input, "model", AliyunImageMetadata.DefaultModel)!; invocation.Model = model;
        var prompt = ImageParameterValues.Text(input, "prompt"); var size = ImageParameterValues.Text(parameters, "size", "1280*1280")!; var count = ImageParameterValues.Integer(parameters, "num_images", 1);
        var negativePrompt = ImageParameterValues.Text(parameters, "negative_prompt"); var promptExtend = ImageParameterValues.Boolean(parameters, "prompt_extend", true); var watermark = ImageParameterValues.Boolean(parameters, "watermark", false);
        var seed = parameters["seed"] is null ? (int?)null : ImageParameterValues.Integer(parameters, "seed", 0); Validate(task, model, prompt, size, count, seed);
        var apiKey = direct.GetValueOrDefault("api-key"); var url = direct.GetValueOrDefault("api-url") ?? AliyunImageMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback) || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.UserInfo.Length > 0) throw new SkillException("invalid_parameter", "必须提供 api-key 及合法 HTTPS api-url；仅本机回环地址允许 HTTP。");
        var output = ImageParameterValues.Text(execution, "output_dir"); if (!tryRun && string.IsNullOrWhiteSpace(output)) throw new SkillException("invalid_parameter", "正式生成必须指定 output-dir。");
        var resolvedOutput = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);
        foreach (var name in parameters.Select(pair => pair.Key).ToArray()) if (name is not ("size" or "num_images" or "negative_prompt" or "prompt_extend" or "watermark" or "seed")) { Ignore("/parameters/" + EscapePointer(name), "unsupported_parameter"); parameters.Remove(name); }
        if (input["attachments"] is JsonArray attachments) { for (var index = 0; index < attachments.Count; index++) Ignore("/attachments/" + index, "unsupported_attachment_role"); input["attachments"] = new JsonArray(); }
        invocation.ThrowIfCancellationRequested();
        return new ImageGenerationOptions(id!, task, model, prompt!, apiKey, endpoint, resolvedOutput, "auto", "auto", count, "png", negativePrompt, size, promptExtend, watermark, seed, timeout, tryRun, verbose, input);
        void Ignore(string pointer, string reason) => invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S(pointer)), ("reason", S(reason))));
    }
    private static void MergeProviderParameters(JsonObject input, JsonObject parameters)
    {
        if (input["provider_parameters"] is not JsonObject providers) return;
        if (providers.Count == 0) { input.Remove("provider_parameters"); return; }
        foreach (var provider in providers)
        {
            if (provider.Key != "aliyun") throw new SkillException("invalid_parameter", "请求包含不属于阿里云的 Provider 参数。");
            if (provider.Value is not JsonObject values) throw new SkillException("invalid_parameter", "aliyun Provider 参数必须是对象。");
            foreach (var value in values)
            {
                if (parameters.ContainsKey(value.Key)) throw new SkillException("invalid_parameter", "Provider 参数与通用参数重复。");
                parameters[value.Key] = value.Value?.DeepClone();
            }
        }
        input.Remove("provider_parameters");
    }

    private static void Validate(string task, string model, string? prompt, string size, int count, int? seed)
    {
        if (task != "text-to-image" || string.IsNullOrWhiteSpace(prompt) || !ModelIds.IsValid(model) || !AliyunImageMetadata.SupportedSizes.Contains(size, StringComparer.Ordinal) || count is < 1 or > 4 || seed is < 0) throw new SkillException("invalid_parameter", "任务、模型、提示词、size、生成数量或 seed 不合法。");
    }
}

