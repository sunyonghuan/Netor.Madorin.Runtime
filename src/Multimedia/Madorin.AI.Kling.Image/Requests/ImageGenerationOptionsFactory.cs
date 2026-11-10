using System.Globalization;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Image.Metadata;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Image.Requests;

internal sealed class ImageGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> Allowed = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir", "timeout",
        "try-run", "verbose", "no-wait", "output-format", "provider-kling-negative-prompt", "provider-kling-resolution",
        "provider-kling-count", "provider-kling-aspect-ratio", "provider-kling-watermark"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);

    public async Task<ImageGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = ImageAttachments.Extract(args);
        var direct = CommandLineParser.Parse(extracted.Remaining, Allowed, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout)) invocation.SetTimeout(CommandLineParser.ParsePositiveInt(directTimeout, 300, "timeout"));
        var fileName = direct.GetValueOrDefault("request-file");
        ImageRequestFile? file = fileName is null ? null : await new KlingImageRequestReader(paths, invocation).ReadAsync(fileName, invocation.CancellationToken);
        var fileId = file is null ? null : ParameterValues.Text(file.Content, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId)
        {
            throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。");
        }

        invocation.ResponseId = id;
        if (file is not null)
        {
            ImageAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory);
            if (file.Content["execution"] is JsonObject controls && controls.ContainsKey("output_dir")) paths.Resolve(ParameterValues.Text(controls, "output_dir")!, file.BaseDirectory);
        }

        ImageAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = ImageInputEcho.Build(direct, file);
        if (extracted.Attachments is not null) input["attachments"] = extracted.Attachments;
        input["request_id"] = S(id);
        var execution = input["execution"]!.AsObject();
        var parameters = input["parameters"]!.AsObject();
        invocation.TryRun = ParameterValues.Boolean(execution, "try_run");
        invocation.Verbose = ParameterValues.Boolean(execution, "verbose");
        var timeout = ParameterValues.PositiveNumber(execution, "timeout", 300);
        invocation.SetTimeout(timeout);
        if (ParameterValues.Text(execution, "output_format", "json") != "json") throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        var task = ParameterValues.Text(input, "task", "text-to-image")!;
        var model = ParameterValues.Text(input, "model", KlingImageMetadata.DefaultModel)!;
        var prompt = ParameterValues.Text(input, "prompt");
        invocation.Model = model;
        if (task != "text-to-image" || !ModelIds.IsValid(model) || string.IsNullOrWhiteSpace(prompt) || prompt.EnumerateRunes().Count() > 2500)
        {
            throw new SkillException("invalid_parameter", "任务、模型或提示词不合法。提示词不能超过 2500 个码点。");
        }

        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? KlingImageMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !TryEndpoint(url, out var endpoint))
        {
            throw new SkillException("invalid_parameter", "必须提供 api-key 和有效 HTTPS API 地址。");
        }

        var noWait = ParameterValues.Boolean(execution, "no_wait");
        var output = ParameterValues.Text(execution, "output_dir");
        if (!invocation.TryRun && !noWait && string.IsNullOrWhiteSpace(output)) throw new SkillException("invalid_parameter", "正式生成必须指定 output-dir。");
        var resolved = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);
        if (input["attachments"] is JsonArray attachments && attachments.Count > 0)
        {
            throw new SkillException("invalid_parameter", "本批可灵图片只支持文生图，不接受参考图。");
        }

        var kling = input["provider_parameters"]?["kling"] as JsonObject;
        var negative = direct.GetValueOrDefault("provider-kling-negative-prompt") ?? (kling is null ? null : ParameterValues.Text(kling, "negative_prompt"));
        if (negative is not null && negative.EnumerateRunes().Count() > 2500) throw new SkillException("invalid_parameter", "negative_prompt 不能超过 2500 个码点。");
        var resolution = direct.GetValueOrDefault("provider-kling-resolution") ?? (kling is null ? null : ParameterValues.Text(kling, "resolution")) ?? KlingImageMetadata.DefaultResolution;
        if (!KlingImageMetadata.Resolutions.Contains(resolution, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "resolution 仅支持 1k 或 2k。");
        var aspect = direct.GetValueOrDefault("provider-kling-aspect-ratio") ?? (kling is null ? null : ParameterValues.Text(kling, "aspect_ratio")) ?? KlingImageMetadata.DefaultAspectRatio;
        if (!KlingImageMetadata.AspectRatios.Contains(aspect, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "aspect_ratio 不属于可灵图片枚举。");
        var count = OptionalCount(kling, direct) ?? 1;
        if (count is < 1 or > 9) throw new SkillException("invalid_parameter", "count 必须为 1 至 9。");
        var watermark = OptionalBool(kling, "watermark_enabled", direct, "provider-kling-watermark");
        RejectUnknown(parameters, []);
        if (kling is not null) RejectUnknown(kling, ["negative_prompt", "resolution", "count", "aspect_ratio", "watermark_enabled"]);
        invocation.ThrowIfCancellationRequested();
        return new ImageGenerationOptions(id!, task, model, prompt, negative, resolution, count, aspect, watermark, apiKey, endpoint!, resolved, noWait, timeout, invocation.TryRun, invocation.Verbose, input);
    }

    public static bool TryEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return false;
        endpoint = uri;
        return true;
    }

    private static int? OptionalCount(JsonObject? source, Dictionary<string, string?> direct)
    {
        if (direct.TryGetValue("provider-kling-count", out var text))
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var directCount) ? directCount : throw new SkillException("invalid_parameter", "count 必须为整数。");
        }

        if (source?["count"] is null) return null;
        if (source["count"] is JsonValue value && value.TryGetValue<int>(out var count)) return count;
        throw new SkillException("invalid_parameter", "count 必须为整数。");
    }

    private static bool? OptionalBool(JsonObject? source, string name, Dictionary<string, string?> direct, string option)
    {
        if (direct.TryGetValue(option, out var text)) return text == "true" ? true : text == "false" ? false : throw new SkillException("invalid_parameter", option + " 必须为 true 或 false。");
        if (source?[name] is null) return null;
        if (source[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        throw new SkillException("invalid_parameter", name + " 必须为布尔值。");
    }

    private static void RejectUnknown(JsonObject source, string[] allowed)
    {
        foreach (var pair in source)
        {
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "参数 " + pair.Key + " 不属于本批可灵图片契约。");
        }
    }
}
