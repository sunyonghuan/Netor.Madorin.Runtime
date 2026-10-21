using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Image.Metadata;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Image.Requests;

internal sealed class ImageGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> AllowedOptions = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir",
        "size", "width", "height", "aspect-ratio", "seed", "style", "negative-prompt", "reference-weight", "guidance-scale", "steps",
        "quality", "num-images", "output-format", "provider-openai-output-format", "timeout", "try-run", "verbose", "no-wait"], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);

    public async Task<ImageGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = ImageAttachments.Extract(args);
        var direct = CommandLineParser.Parse(extracted.Remaining, AllowedOptions, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("try-run", out var directTryRun))
        {
            invocation.TryRun = directTryRun == "true";
        }

        if (direct.TryGetValue("timeout", out var directTimeout))
        {
            invocation.SetTimeout(ParsePositiveInt(directTimeout, 300, "timeout"));
        }

        var fileName = direct.GetValueOrDefault("request-file");
        ImageRequestFile? file = null;
        if (fileName is not null)
        {
            file = await new OpenAiImageRequestReader(paths, invocation).ReadAsync(fileName, invocation.CancellationToken,
                direct.ContainsKey("try-run") ? directTryRun == "true" : null);
        }

        var fileId = file is null ? null : ImageParameterValues.Text(file.Content, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId)
        {
            throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。");
        }

        invocation.ResponseId = id;
        if (file is not null)
        {
            ImageAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory);
            if (file.Content["execution"] is JsonObject fileControls && fileControls.ContainsKey("output_dir"))
            {
                paths.Resolve(ImageParameterValues.Text(fileControls, "output_dir")!, file.BaseDirectory);
            }
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
        if (ImageParameterValues.Boolean(execution, "no_wait"))
        {
            throw new SkillException("invalid_parameter", "OpenAI 图片生成当前不支持 no_wait=true。");
        }
        if (ImageParameterValues.Text(execution, "output_format", "json") != "json")
        {
            throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        }

        var task = ImageParameterValues.Text(input, "task", "text-to-image")!;
        var model = ImageParameterValues.Text(input, "model", "gpt-image-1")!;
        invocation.Model = model;
        var prompt = ImageParameterValues.Text(input, "prompt");
        var size = ConvertDimensions(parameters, invocation);
        var quality = ImageParameterValues.Text(parameters, "quality", "auto")!;
        var format = ConvertOutputFormat(input, parameters, invocation);
        var count = ImageParameterValues.Integer(parameters, "num_images", 1);
        Validate(task, model, prompt, size, quality, format, count);

        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? "https://api.openai.com/v1";
        if (string.IsNullOrWhiteSpace(apiKey) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback) ||
            endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.UserInfo.Length > 0)
        {
            throw new SkillException("invalid_parameter", "必须提供 api-key 及不含用户信息、查询参数或片段的 HTTPS API 地址；仅本机回环地址允许 HTTP。");
        }

        var output = ImageParameterValues.Text(execution, "output_dir");
        if (!tryRun && string.IsNullOrWhiteSpace(output))
        {
            throw new SkillException("invalid_parameter", "正式生成必须指定 output-dir。");
        }

        string? resolvedOutput = null;
        if (output is not null)
        {
            resolvedOutput = paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);
        }

        foreach (var name in parameters.Select(pair => pair.Key).ToArray())
        {
            if (name is not ("size" or "width" or "height" or "quality" or "num_images" or "output_format"))
            {
                Ignore("/parameters/" + EscapePointer(name), "unsupported_parameter");
                parameters.Remove(name);
            }
        }

        if (input["provider_parameters"] is JsonObject providers)
        {
            foreach (var branch in providers.ToArray())
            {
                var values = branch.Value!.AsObject();
                foreach (var name in values.Select(pair => pair.Key).ToArray())
                {
                    if (branch.Key != "openai" || name != "output_format")
                    {
                        Ignore("/provider_parameters/" + EscapePointer(branch.Key) + "/" + EscapePointer(name), branch.Key == "openai" ? "unsupported_parameter" : "other_provider");
                        values.Remove(name);
                    }
                }
                if (values.Count == 0) { providers.Remove(branch.Key); }
            }
            if (providers.Count == 0) { input.Remove("provider_parameters"); }
        }
        if (input["attachments"] is JsonArray attachments)
        {
            for (var index = 0; index < attachments.Count; index++) { Ignore("/attachments/" + index, "unsupported_attachment_role"); }
            input["attachments"] = new JsonArray();
        }
        invocation.ThrowIfCancellationRequested();
        return new ImageGenerationOptions(id!, task, model, prompt!, apiKey, endpoint, resolvedOutput, size, quality, count, format, timeout, tryRun, verbose, input);

        void Ignore(string pointer, string reason) => invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S(pointer)), ("reason", S(reason))));
    }

    private static string ConvertDimensions(JsonObject parameters, SkillInvocation invocation)
    {
        var size = ImageParameterValues.Text(parameters, "size");
        if (parameters.ContainsKey("width") || parameters.ContainsKey("height"))
        {
            var width = ImageParameterValues.Integer(parameters, "width", 0);
            var height = ImageParameterValues.Integer(parameters, "height", 0);
            if (width == 0 || height == 0) { throw new SkillException("invalid_parameter", "width 与 height 必须成对提供。"); }
            var converted = FormattableString.Invariant($"{width}x{height}");
            if (size is not null && size != converted) { throw new SkillException("invalid_parameter", "width/height 与 size 冲突。"); }
            size = converted;
            if (!OpenAiImageMetadata.SupportedSizes.Contains(converted, StringComparer.Ordinal)) { throw new SkillException("invalid_parameter", "width/height 不属于支持的精确尺寸。"); }
            invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("dimensions_to_size")), ("source_parameters", Strings("/parameters/width", "/parameters/height")), ("target_parameters", Strings("/parameters/size"))));
        }
        return size ?? "auto";
    }

    private static string ConvertOutputFormat(JsonObject input, JsonObject parameters, SkillInvocation invocation)
    {
        var target = input["provider_parameters"]?["openai"] is JsonObject provider ? ImageParameterValues.Text(provider, "output_format") : null;
        if (parameters.ContainsKey("output_format"))
        {
            var source = ImageParameterValues.Text(parameters, "output_format");
            if (source is not ("png" or "jpeg" or "webp") || target is not null && source != target)
            {
                throw new SkillException("invalid_parameter", "图片编码格式无效或与 OpenAI 编码格式冲突。");
            }
            target = source;
            invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("image_format_to_openai")), ("source_parameters", Strings("/parameters/output_format")),
                ("target_parameters", Strings("/provider_parameters/openai/output_format"))));
        }
        return target ?? "png";
    }

    private static void Validate(string task, string model, string? prompt, string size, string quality, string format, int count)
    {
        if (task != "text-to-image" || string.IsNullOrWhiteSpace(prompt) || !ModelIds.IsValid(model))
        {
            throw new SkillException("invalid_parameter", "任务、模型或提示词不合法。");
        }

        if (!OpenAiImageMetadata.SupportedSizes.Contains(size, StringComparer.Ordinal) ||
            !OpenAiImageMetadata.SupportedQualities.Contains(quality, StringComparer.Ordinal) ||
            format is not ("png" or "jpeg" or "webp") || count is < 1 or > 10)
        {
            throw new SkillException("invalid_parameter", "图片尺寸、质量、格式或生成数量不合法。");
        }
    }
}
