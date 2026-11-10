using System.Globalization;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Video.Metadata;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Video.Requests;

internal sealed class VideoGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> Allowed = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir", "timeout",
        "try-run", "verbose", "no-wait", "output-format", "duration", "resolution", "reference-image", "image-url",
        "provider-kling-aspect-ratio", "provider-kling-watermark"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);

    public async Task<VideoGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = VideoAttachments.Extract(args);
        var direct = CommandLineParser.Parse(extracted.Remaining, Allowed, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout)) invocation.SetTimeout(CommandLineParser.ParsePositiveInt(directTimeout, 300, "timeout"));
        var fileName = direct.GetValueOrDefault("request-file");
        VideoRequestFile? file = fileName is null ? null : await new KlingVideoRequestReader(paths, invocation).ReadAsync(fileName, invocation.CancellationToken);
        var fileId = file is null ? null : ParameterValues.Text(file.Content, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId)
        {
            throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。");
        }

        invocation.ResponseId = id;
        string? imageBase = null;
        if (file is not null)
        {
            imageBase = file.BaseDirectory;
            VideoAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory);
            if (file.Content["execution"] is JsonObject controls && controls.ContainsKey("output_dir")) paths.Resolve(ParameterValues.Text(controls, "output_dir")!, file.BaseDirectory);
        }

        VideoAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = VideoInputEcho.Build(direct, file);
        if (extracted.Attachments is not null) input["attachments"] = extracted.Attachments;
        input["request_id"] = S(id);
        var execution = input["execution"]!.AsObject();
        var parameters = input["parameters"]!.AsObject();
        invocation.TryRun = ParameterValues.Boolean(execution, "try_run");
        invocation.Verbose = ParameterValues.Boolean(execution, "verbose");
        var timeout = ParameterValues.PositiveNumber(execution, "timeout", 300);
        invocation.SetTimeout(timeout);
        if (ParameterValues.Text(execution, "output_format", "json") != "json") throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        var task = ParameterValues.Text(input, "task", "text-to-video")!;
        var model = ParameterValues.Text(input, "model", KlingVideoMetadata.DefaultModel)!;
        var prompt = ParameterValues.Text(input, "prompt");
        invocation.Model = model;
        if (task is not ("text-to-video" or "image-to-video") || !ModelIds.IsValid(model) || model.Any(character => character is '/' or '\\' or '?' or '#'))
        {
            throw new SkillException("invalid_parameter", "任务或模型不合法。");
        }

        if (task == "text-to-video" && string.IsNullOrWhiteSpace(prompt)) throw new SkillException("invalid_parameter", "文生视频必须提供提示词。");
        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? KlingVideoMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !TryEndpoint(url, out var endpoint))
        {
            throw new SkillException("invalid_parameter", "必须提供 api-key 和有效 HTTPS API 地址。");
        }

        var noWait = ParameterValues.Boolean(execution, "no_wait");
        var output = ParameterValues.Text(execution, "output_dir");
        if (!invocation.TryRun && !noWait && string.IsNullOrWhiteSpace(output)) throw new SkillException("invalid_parameter", "正式生成必须指定 output-dir。");
        var resolved = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);
        var image = ReadImage(input["attachments"] as JsonArray, task, direct.ContainsKey("reference-image") ? null : imageBase, direct);
        if (task == "image-to-video" && image.Kind is null) throw new SkillException("invalid_parameter", "图生视频必须提供一张首帧。");
        var duration = OptionalInt(parameters, direct, "duration");
        if (duration is < 3 or > 30) throw new SkillException("invalid_parameter", "duration 仅支持 3 至 30 秒。");
        var resolution = direct.GetValueOrDefault("resolution") ?? ParameterValues.Text(parameters, "resolution") ?? KlingVideoMetadata.DefaultResolution;
        if (!KlingVideoMetadata.Resolutions.Contains(resolution, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "resolution 仅支持 720p 或 1080p。");
        var kling = input["provider_parameters"]?["kling"] as JsonObject;
        var aspect = direct.GetValueOrDefault("provider-kling-aspect-ratio") ?? (kling is null ? null : ParameterValues.Text(kling, "aspect_ratio"));
        if (task == "image-to-video" && aspect is not null) throw new SkillException("invalid_parameter", "图生视频不接受 aspect_ratio。");
        aspect ??= task == "text-to-video" ? KlingVideoMetadata.DefaultAspectRatio : null;
        if (aspect is not null && !KlingVideoMetadata.AspectRatios.Contains(aspect, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "aspect_ratio 不属于 Kling 4.0 文生视频枚举。");
        var watermark = OptionalBool(kling, "watermark_enabled", direct, "provider-kling-watermark");
        RejectUnknown(parameters, ["duration", "resolution"]);
        if (kling is not null) RejectUnknown(kling, ["aspect_ratio", "watermark_enabled"]);
        parameters["resolution"] = S(resolution);
        if (duration is null) parameters.Remove("duration");
        invocation.ThrowIfCancellationRequested();
        return new VideoGenerationOptions(id!, task, model, string.IsNullOrWhiteSpace(prompt) ? null : prompt, duration, resolution, aspect, watermark, image.Kind, image.Value, image.Kind == "path" ? image.BaseDirectory : null, apiKey, endpoint!, resolved, noWait, timeout, invocation.TryRun, invocation.Verbose, input);
    }

    public static bool TryEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return false;
        endpoint = uri;
        return true;
    }

    private static (string? Kind, string? Value, string? BaseDirectory) ReadImage(JsonArray? attachments, string task, string? baseDirectory, Dictionary<string, string?> direct)
    {
        var hasDirect = direct.ContainsKey("image-url") || direct.ContainsKey("reference-image");
        if (task != "image-to-video" && (hasDirect || attachments is { Count: > 0 })) throw new SkillException("invalid_parameter", "文生视频不接受图片。");
        if (task != "image-to-video") return (null, null, null);
        if (hasDirect && attachments is { Count: > 0 }) throw new SkillException("invalid_parameter", "图片来源只能选择一处。");
        if (direct.TryGetValue("image-url", out var remote))
        {
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new SkillException("invalid_parameter", "image-url 必须为不含用户信息的 HTTPS 地址。");
            }

            return ("uri", remote, null);
        }

        if (direct.TryGetValue("reference-image", out var path)) return ("path", path, null);
        if (attachments is null || attachments.Count == 0) return (null, null, null);
        if (attachments.Count != 1 || attachments[0] is not JsonObject attachment) throw new SkillException("invalid_parameter", "图生视频只接受一张首帧。");
        var role = attachment["role"]!.GetValue<string>();
        if (role is "first_frame" or "middle_frame" or "last_frame") throw new SkillException("invalid_parameter", "本批只接受 reference 或 source 角色的单张首帧，不接受多关键帧角色。");
        if (role is not ("reference" or "source")) throw new SkillException("invalid_parameter", "图生视频附件角色必须是 reference 或 source。");
        var entry = attachment["source"]!.AsObject().First();
        if (entry.Key == "uri")
        {
            var value = entry.Value!.GetValue<string>();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) throw new SkillException("invalid_parameter", "参考图 URL 必须为 HTTPS。");
            return ("uri", value, null);
        }

        if (entry.Key != "path") throw new SkillException("invalid_parameter", "参考图只接受 path 或 uri。");
        return ("path", entry.Value!.GetValue<string>(), baseDirectory);
    }

    private static int? OptionalInt(JsonObject parameters, Dictionary<string, string?> direct, string name)
    {
        var text = direct.GetValueOrDefault(name) ?? NumberText(parameters, name);
        if (text is null) return null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new SkillException("invalid_parameter", name + " 必须为整数。");
    }

    private static string? NumberText(JsonObject parameters, string name) => parameters[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : ParameterValues.Text(parameters, name);

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
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "参数 " + pair.Key + " 不属于本批可灵视频契约。");
        }
    }
}
