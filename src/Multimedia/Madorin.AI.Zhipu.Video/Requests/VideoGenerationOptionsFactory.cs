using System.Globalization;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Video.Metadata;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Video.Requests;

internal sealed class VideoGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> Allowed = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir", "timeout",
        "try-run", "verbose", "no-wait", "output-format", "duration", "fps", "size", "width", "height",
        "provider-zhipu-quality", "provider-zhipu-with-audio", "provider-zhipu-watermark", "provider-zhipu-user-id"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait"], StringComparer.Ordinal);

    public async Task<VideoGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = RequestAttachments.Extract(args);
        var direct = CommandLineParser.Parse(extracted.Remaining, Allowed, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout)) invocation.SetTimeout(CommandLineParser.ParsePositiveInt(directTimeout, 300, "timeout"));
        var fileName = direct.GetValueOrDefault("request-file");
        JsonObject? file = null;
        string? baseDirectory = null;
        if (fileName is not null)
        {
            var read = await new GenerationRequestReader(paths, invocation).ReadAsync(fileName, invocation.CancellationToken, direct.TryGetValue("try-run", out var tryRunText) ? tryRunText == "true" : null);
            file = read.Content;
            baseDirectory = read.BaseDirectory;
            RequestAttachments.Validate(file["attachments"], file.ContainsKey("attachments"), paths, baseDirectory);
        }

        RequestAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = Echo(direct, file);
        if (extracted.Attachments is not null) input["attachments"] = extracted.Attachments;
        var fileId = file is null ? null : ParameterValues.Text(file, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId)
        {
            throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。");
        }

        invocation.ResponseId = id;
        input["request_id"] = S(id);
        var execution = input["execution"]!.AsObject();
        var parameters = input["parameters"]!.AsObject();
        invocation.TryRun = ParameterValues.Boolean(execution, "try_run");
        invocation.Verbose = ParameterValues.Boolean(execution, "verbose");
        if (ParameterValues.Text(execution, "output_format", "json") != "json") throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        var noWait = ParameterValues.Boolean(execution, "no_wait");
        var task = ParameterValues.Text(input, "task", "text-to-video")!;
        var model = ParameterValues.Text(input, "model", ZhipuVideoMetadata.DefaultModel)!;
        invocation.Model = model;
        if (task is not ("text-to-video" or "image-to-video") || !ModelIds.IsValid(model)) throw new SkillException("invalid_parameter", "任务或模型不合法。");
        var prompt = ParameterValues.Text(input, "prompt");
        if (prompt is not null && prompt.EnumerateRunes().Count() > 512) throw new SkillException("invalid_parameter", "视频提示词不能超过 512 个码点。");
        if (task == "text-to-video" && string.IsNullOrWhiteSpace(prompt)) throw new SkillException("invalid_parameter", "文生视频必须提供提示词。");
        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? ZhipuVideoMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !TryEndpoint(url, out var endpoint)) throw new SkillException("invalid_parameter", "必须提供 api-key 和有效 HTTPS API 地址。");
        var output = ParameterValues.Text(execution, "output_dir");
        if (!invocation.TryRun && !noWait && string.IsNullOrWhiteSpace(output)) throw new SkillException("invalid_parameter", "等待完成的正式生成必须指定 output-dir。");
        var resolved = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : baseDirectory);
        var imageBase = extracted.Attachments is not null ? null : baseDirectory;
        var image = ReadImage(input["attachments"] as JsonArray, task, imageBase);
        if (task == "image-to-video" && image.Kind is null && string.IsNullOrWhiteSpace(prompt)) throw new SkillException("invalid_parameter", "图生视频至少需要提示词或一张参考图。");
        var duration = OptionalInt(parameters, direct, "duration") ?? 5;
        var fps = OptionalInt(parameters, direct, "fps") ?? 30;
        if (duration is not (5 or 10) || fps is not (30 or 60)) throw new SkillException("invalid_parameter", "duration 仅支持 5 或 10，fps 仅支持 30 或 60。");
        var size = ResolveSize(parameters, direct, invocation);
        var zhipu = input["provider_parameters"]?["zhipu"] as JsonObject;
        var quality = direct.GetValueOrDefault("provider-zhipu-quality") ?? (zhipu is null ? null : ParameterValues.Text(zhipu, "quality"));
        if (quality is not null and not ("speed" or "quality")) throw new SkillException("invalid_parameter", "智谱视频 quality 仅支持 speed 或 quality。");
        var withAudio = OptionalBool(zhipu, "with_audio", direct, "provider-zhipu-with-audio");
        var watermark = OptionalBool(zhipu, "watermark_enabled", direct, "provider-zhipu-watermark");
        var userId = direct.GetValueOrDefault("provider-zhipu-user-id") ?? (zhipu is null ? null : ParameterValues.Text(zhipu, "user_id"));
        if (userId is not null && (userId.Length is < 6 or > 128 || userId.Any(char.IsControl))) throw new SkillException("invalid_parameter", "user_id 长度必须为 6 至 128 且不含控制字符。");
        RejectUnknown(parameters, ["duration", "fps", "size", "width", "height"]);
        if (zhipu is not null) RejectUnknown(zhipu, ["quality", "with_audio", "watermark_enabled", "user_id"]);
        parameters.Remove("width");
        parameters.Remove("height");
        parameters["duration"] = N(duration);
        parameters["fps"] = N(fps);
        if (size is not null) parameters["size"] = S(size);
        invocation.ThrowIfCancellationRequested();
        return new VideoGenerationOptions(id!, task, model, string.IsNullOrWhiteSpace(prompt) ? null : prompt, apiKey, endpoint!, resolved, duration, fps, size, quality, withAudio, watermark, userId, image.Kind, image.Value, image.Kind == "path" ? image.BaseDirectory : null, noWait, timeout, invocation.TryRun, invocation.Verbose, input);
    }

    public static bool TryEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return false;
        endpoint = uri;
        return true;
    }

    private static (string? Kind, string? Value, string? BaseDirectory) ReadImage(JsonArray? attachments, string task, string? baseDirectory)
    {
        if (attachments is null || attachments.Count == 0) return (null, null, null);
        if (task != "image-to-video") throw new SkillException("invalid_parameter", "文生视频不接受图片附件。");
        if (attachments.Count != 1 || attachments[0] is not JsonObject attachment) throw new SkillException("invalid_parameter", "图生视频只接受一张参考图。");
        var role = attachment["role"]!.GetValue<string>();
        if (role is "first_frame" or "last_frame") throw new SkillException("invalid_parameter", "本批不支持首尾帧。");
        if (role is not ("reference" or "source")) throw new SkillException("invalid_parameter", "图生视频附件角色必须是 reference 或 source。");
        var entry = attachment["source"]!.AsObject().First();
        return (entry.Key, entry.Value!.GetValue<string>(), entry.Key == "path" ? baseDirectory : null);
    }

    private static string? ResolveSize(JsonObject parameters, Dictionary<string, string?> direct, SkillInvocation invocation)
    {
        var size = direct.GetValueOrDefault("size") ?? ParameterValues.Text(parameters, "size");
        var width = direct.GetValueOrDefault("width") ?? NumberText(parameters, "width");
        var height = direct.GetValueOrDefault("height") ?? NumberText(parameters, "height");
        if ((width is null) != (height is null)) throw new SkillException("invalid_parameter", "width 与 height 必须成对提供。");
        if (width is not null)
        {
            if (!int.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out var widthValue) || !int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out var heightValue))
            {
                throw new SkillException("invalid_parameter", "width/height 必须为整数。");
            }

            var converted = FormattableString.Invariant($"{widthValue}x{heightValue}");
            if (size is not null && size.Replace('×', 'x').Replace('*', 'x').Replace('X', 'x') != converted) throw new SkillException("invalid_parameter", "width/height 与 size 冲突。");
            invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("dimensions_to_size")), ("source_parameters", Strings("/parameters/width", "/parameters/height")), ("target_parameters", Strings("/parameters/size"))));
            size = converted;
        }

        if (size is null) return null;
        size = size.Replace('×', 'x').Replace('*', 'x').Replace('X', 'x');
        if (!ZhipuVideoMetadata.Sizes.Contains(size, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "size 不属于 CogVideoX-3 枚举。");
        return size;
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
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "参数 " + pair.Key + " 不属于本批 CogVideoX 契约。");
        }
    }

    private static JsonObject Echo(Dictionary<string, string?> direct, JsonObject? file)
    {
        var input = file?.DeepClone().AsObject() ?? new JsonObject();
        var parameters = input["parameters"]?.AsObject() ?? new JsonObject();
        var execution = input["execution"]?.AsObject() ?? new JsonObject();
        var providers = input["provider_parameters"]?.AsObject() ?? new JsonObject();
        foreach (var pair in direct)
        {
            var name = pair.Key.Replace('-', '_');
            switch (name)
            {
                case "api_key" or "request_file" or "api_url": break;
                case "request_id" or "prompt" or "task" or "model": input[name] = S(pair.Value); break;
                case "try_run" or "verbose" or "no_wait": execution[name] = B(pair.Value == "true"); break;
                case "timeout": execution[name] = N(CommandLineParser.ParsePositiveInt(pair.Value, 300, name)); break;
                case "output_format" or "output_dir": execution[name] = S(pair.Value); break;
                case "duration" or "fps" or "width" or "height": parameters[name] = N(long.Parse(pair.Value!, CultureInfo.InvariantCulture)); break;
                case "size": parameters[name] = S(pair.Value); break;
                case "provider_zhipu_quality": Zhipu(providers)["quality"] = S(pair.Value); break;
                case "provider_zhipu_with_audio": Zhipu(providers)["with_audio"] = B(pair.Value == "true"); break;
                case "provider_zhipu_watermark": Zhipu(providers)["watermark_enabled"] = B(pair.Value == "true"); break;
                case "provider_zhipu_user_id": Zhipu(providers)["user_id"] = S(pair.Value); break;
            }
        }

        input["parameters"] = parameters;
        input["execution"] = execution;
        if (providers.Count > 0) input["provider_parameters"] = providers;
        return input;
    }

    private static JsonObject Zhipu(JsonObject providers)
    {
        if (providers["zhipu"] is not JsonObject zhipu)
        {
            zhipu = new JsonObject();
            providers["zhipu"] = zhipu;
        }
        return zhipu;
    }
}
