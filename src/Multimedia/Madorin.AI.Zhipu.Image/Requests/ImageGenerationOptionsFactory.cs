using System.Globalization;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Image.Metadata;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Image.Requests;

internal sealed class ImageGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> Allowed = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir", "timeout",
        "try-run", "verbose", "no-wait", "output-format", "size", "width", "height", "quality",
        "provider-zhipu-watermark", "provider-zhipu-user-id"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);

    public async Task<ImageGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = ImageAttachments.Extract(args);
        var direct = CommandLineParser.Parse(extracted.Remaining, Allowed, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout)) invocation.SetTimeout(CommandLineParser.ParsePositiveInt(directTimeout, 300, "timeout"));
        var fileName = direct.GetValueOrDefault("request-file");
        ImageRequestFile? file = fileName is null ? null : await new ZhipuImageRequestReader(paths, invocation).ReadAsync(fileName, invocation.CancellationToken, direct.TryGetValue("try-run", out var tryRunText) ? tryRunText == "true" : null);
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
            if (file.Content["execution"] is JsonObject controls && controls.ContainsKey("output_dir")) paths.Resolve(ImageParameterValues.Text(controls, "output_dir")!, file.BaseDirectory);
        }

        ImageAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = ImageInputEcho.Build(direct, file);
        if (extracted.Attachments is not null) input["attachments"] = extracted.Attachments;
        input["request_id"] = S(id);
        var execution = input["execution"]!.AsObject();
        var parameters = input["parameters"]!.AsObject();
        invocation.TryRun = ImageParameterValues.Boolean(execution, "try_run");
        invocation.Verbose = ImageParameterValues.Boolean(execution, "verbose");
        var timeout = ImageParameterValues.Integer(execution, "timeout", 300);
        invocation.SetTimeout(timeout);
        if (ImageParameterValues.Boolean(execution, "no_wait")) throw new SkillException("invalid_parameter", "CogView 同步图片不接受 no_wait=true。");
        if (ImageParameterValues.Text(execution, "output_format", "json") != "json") throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        var task = ImageParameterValues.Text(input, "task", "text-to-image")!;
        var model = ImageParameterValues.Text(input, "model", ZhipuImageMetadata.DefaultModel)!;
        var prompt = ImageParameterValues.Text(input, "prompt");
        invocation.Model = model;
        if (task != "text-to-image" || !ModelIds.IsValid(model) || string.IsNullOrWhiteSpace(prompt))
        {
            throw new SkillException("invalid_parameter", "任务、模型或提示词不合法。");
        }

        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? ZhipuImageMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !TryEndpoint(url, out var endpoint))
        {
            throw new SkillException("invalid_parameter", "必须提供 api-key 和有效 HTTPS API 地址。");
        }

        var output = ImageParameterValues.Text(execution, "output_dir");
        if (!invocation.TryRun && string.IsNullOrWhiteSpace(output)) throw new SkillException("invalid_parameter", "正式生成必须指定 output-dir。");
        var resolved = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);
        if (input["attachments"] is JsonArray attachments && attachments.Count > 0)
        {
            throw new SkillException("invalid_parameter", "本批 CogView 只支持文生图，不接受图片附件。");
        }

        var size = ResolveSize(parameters, direct, invocation);
        var quality = direct.GetValueOrDefault("quality") ?? ImageParameterValues.Text(parameters, "quality") ?? ZhipuImageMetadata.DefaultQuality;
        if (quality is not ("standard" or "hd")) throw new SkillException("invalid_parameter", "quality 仅支持 standard 或 hd。");
        var zhipu = input["provider_parameters"]?["zhipu"] as JsonObject;
        bool? watermark = OptionalBool(zhipu, "watermark_enabled", direct, "provider-zhipu-watermark");
        var userId = direct.GetValueOrDefault("provider-zhipu-user-id") ?? (zhipu is null ? null : ImageParameterValues.Text(zhipu, "user_id"));
        if (userId is not null && (userId.Length is < 6 or > 128 || userId.Any(char.IsControl)))
        {
            throw new SkillException("invalid_parameter", "user_id 长度必须为 6 至 128 且不含控制字符。");
        }

        RejectUnknown(parameters, ["size", "width", "height", "quality"]);
        if (zhipu is not null) RejectUnknown(zhipu, ["watermark_enabled", "user_id"]);
        parameters.Remove("width");
        parameters.Remove("height");
        parameters["size"] = S(size);
        parameters["quality"] = S(quality);
        invocation.ThrowIfCancellationRequested();
        return new ImageGenerationOptions(id!, task, model, prompt!, apiKey, endpoint!, resolved, size, quality, watermark, userId, timeout, invocation.TryRun, invocation.Verbose, input);
    }

    public static bool TryEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        endpoint = uri;
        return true;
    }

    private static string ResolveSize(JsonObject parameters, Dictionary<string, string?> direct, SkillInvocation invocation)
    {
        var size = direct.GetValueOrDefault("size") ?? ImageParameterValues.Text(parameters, "size");
        var widthText = direct.GetValueOrDefault("width") ?? NumberText(parameters, "width");
        var heightText = direct.GetValueOrDefault("height") ?? NumberText(parameters, "height");
        if ((widthText is null) != (heightText is null)) throw new SkillException("invalid_parameter", "width 与 height 必须成对提供。");
        if (widthText is not null)
        {
            if (!int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(heightText, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            {
                throw new SkillException("invalid_parameter", "width/height 必须为整数。");
            }

            var converted = FormattableString.Invariant($"{width}x{height}");
            if (size is not null && NormalizeSize(size) != converted) throw new SkillException("invalid_parameter", "width/height 与 size 冲突。");
            invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("dimensions_to_size")), ("source_parameters", Strings("/parameters/width", "/parameters/height")), ("target_parameters", Strings("/parameters/size"))));
            size = converted;
        }

        size = size is null ? ZhipuImageMetadata.DefaultSize : NormalizeSize(size);
        if (!IsCogViewSize(size)) throw new SkillException("invalid_parameter", "size 必须符合 CogView 的边长、16 倍数和像素积限制。");
        return size;
    }

    private static bool IsCogViewSize(string size)
    {
        var parts = size.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        return width is >= 512 and <= 2048 && height is >= 512 and <= 2048 && width % 16 == 0 && height % 16 == 0 && (long)width * height <= 2_097_152;
    }

    private static string NormalizeSize(string size) => size.Replace('×', 'x').Replace('*', 'x').Replace('X', 'x');
    private static string? NumberText(JsonObject parameters, string name) => parameters[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : ImageParameterValues.Text(parameters, name);

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
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "参数 " + pair.Key + " 不属于本批 CogView 契约。");
        }
    }
}
