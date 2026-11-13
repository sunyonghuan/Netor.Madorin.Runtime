using System.Globalization;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Aliyun.Video.Metadata;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Aliyun.Video.Requests;

internal sealed class VideoGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> Allowed = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir", "timeout",
        "try-run", "verbose", "no-wait", "output-format", "duration", "resolution", "aspect-ratio", "negative-prompt",
        "provider-aliyun-prompt-extend", "provider-aliyun-watermark", "provider-aliyun-seed", "provider-aliyun-audio-url"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new([
        "try-run", "verbose", "no-wait", "provider-aliyun-prompt-extend", "provider-aliyun-watermark"
    ], StringComparer.Ordinal);

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
        if (!direct.ContainsKey("timeout") && NumberText(execution, "timeout") is { } fileTimeout)
        {
            invocation.SetTimeout(CommandLineParser.ParsePositiveInt(fileTimeout, 300, "timeout"));
        }

        var timeout = direct.ContainsKey("timeout")
            ? CommandLineParser.ParsePositiveInt(direct["timeout"], 300, "timeout")
            : NumberText(execution, "timeout") is { } storedTimeout
                ? CommandLineParser.ParsePositiveInt(storedTimeout, 300, "timeout")
                : 300;
        invocation.TryRun = ParameterValues.Boolean(execution, "try_run");
        invocation.Verbose = ParameterValues.Boolean(execution, "verbose");
        if (ParameterValues.Text(execution, "output_format", "json") != "json") throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        var noWait = ParameterValues.Boolean(execution, "no_wait");
        var task = ParameterValues.Text(input, "task") ?? "text-to-video";
        var model = ParameterValues.Text(input, "model") ?? (task == "image-to-video" ? AliyunVideoMetadata.DefaultImageModel : AliyunVideoMetadata.DefaultTextModel);
        invocation.Model = model;
        if (task is not ("text-to-video" or "image-to-video") || !ModelIds.IsValid(model)) throw new SkillException("invalid_parameter", "任务或模型不合法。");
        var prompt = ParameterValues.Text(input, "prompt");
        if (prompt is not null && prompt.EnumerateRunes().Count() > 5000) throw new SkillException("invalid_parameter", "视频提示词不能超过 5000 个字符。");
        if (task == "text-to-video" && string.IsNullOrWhiteSpace(prompt)) throw new SkillException("invalid_parameter", "文生视频必须提供提示词。");
        var negative = ParameterValues.Text(parameters, "negative_prompt");
        if (negative is not null && negative.EnumerateRunes().Count() > 500) throw new SkillException("invalid_parameter", "反向提示词不能超过 500 个字符。");
        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? AliyunVideoMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !TryEndpoint(url, out var endpoint)) throw new SkillException("invalid_parameter", "必须提供 api-key 和有效 HTTPS API 地址。");
        var output = ParameterValues.Text(execution, "output_dir");
        if (!invocation.TryRun && !noWait && string.IsNullOrWhiteSpace(output)) throw new SkillException("invalid_parameter", "等待完成的正式生成必须指定 output-dir。");
        var resolved = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : baseDirectory);
        var imageBase = extracted.Attachments is not null ? null : baseDirectory;
        var image = ReadImage(input["attachments"] as JsonArray, task, imageBase);
        RejectUnknown(parameters, ["duration", "resolution", "aspect_ratio", "negative_prompt"]);
        var aliyun = input["provider_parameters"]?["aliyun"] as JsonObject;
        if (input["provider_parameters"] is JsonObject providers && providers.Any(pair => pair.Key != "aliyun")) throw new SkillException("invalid_parameter", "provider_parameters 只接受 aliyun。");
        if (aliyun is not null) RejectUnknown(aliyun, ["prompt_extend", "watermark", "seed", "audio_url"]);
        var duration = ResolveDuration(parameters, direct);
        var resolution = ResolveResolution(parameters, direct, invocation);
        var aspect = ResolveAspect(task, parameters, direct, invocation);
        var promptExtend = ResolveBool(aliyun, "prompt_extend", direct, "provider-aliyun-prompt-extend", true);
        var watermark = ResolveBool(aliyun, "watermark", direct, "provider-aliyun-watermark", false);
        var seed = ResolveSeed(aliyun, direct);
        var audio = ResolveAudio(aliyun, direct);
        invocation.ThrowIfCancellationRequested();
        return new VideoGenerationOptions(id!, task, model, string.IsNullOrWhiteSpace(prompt) ? null : prompt, negative, apiKey, endpoint!, resolved, duration, resolution, aspect, promptExtend, watermark, seed, audio, image.Kind, image.Value, image.Kind == "path" ? image.BaseDirectory : null, noWait, timeout, invocation.TryRun, invocation.Verbose, input);
    }

    public static bool TryEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return false;
        endpoint = uri;
        return true;
    }

    private static bool IsMediaUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;


    private static (string? Kind, string? Value, string? BaseDirectory) ReadImage(JsonArray? attachments, string task, string? baseDirectory)
    {
        if (attachments is null || attachments.Count == 0)
        {
            if (task == "image-to-video") throw new SkillException("invalid_parameter", "图生视频必须提供一张首帧。");
            return (null, null, null);
        }

        if (task != "image-to-video") throw new SkillException("invalid_parameter", "文生视频不接受图片附件。");
        if (attachments.Count != 1 || attachments[0] is not JsonObject attachment) throw new SkillException("invalid_parameter", "图生视频只接受一张首帧。");
        var role = attachment["role"]!.GetValue<string>();
        if (role == "last_frame") throw new SkillException("invalid_parameter", "本批不支持首尾帧或视频续写。");
        if (role is not ("first_frame" or "reference" or "source")) throw new SkillException("invalid_parameter", "图生视频附件角色必须是 first_frame、reference 或 source。");
        var entry = attachment["source"]!.AsObject().First();
        return (entry.Key, entry.Value!.GetValue<string>(), entry.Key == "path" ? baseDirectory : null);
    }

    private static int ResolveDuration(JsonObject parameters, Dictionary<string, string?> direct)
    {
        var text = direct.GetValueOrDefault("duration") ?? NumberText(parameters, "duration");
        if (text is null) return 5;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 2 or > 15)
        {
            throw new SkillException("invalid_parameter", "duration 必须是 2 至 15 的整数秒。");
        }

        return value;
    }

    private static string ResolveResolution(JsonObject parameters, Dictionary<string, string?> direct, SkillInvocation invocation)
    {
        var raw = direct.GetValueOrDefault("resolution") ?? ParameterValues.Text(parameters, "resolution");
        if (raw is null) return "1080P";
        var canonical = raw.ToUpperInvariant();
        if (canonical is not ("720P" or "1080P")) throw new SkillException("invalid_parameter", "resolution 仅支持 720P 或 1080P。");
        if (!string.Equals(raw, canonical, StringComparison.Ordinal))
        {
            invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("resolution_case")), ("source_parameters", Strings("/parameters/resolution")), ("target_parameters", Strings("/parameters/resolution"))));
        }

        return canonical;
    }

    private static string? ResolveAspect(string task, JsonObject parameters, Dictionary<string, string?> direct, SkillInvocation invocation)
    {
        var raw = direct.GetValueOrDefault("aspect-ratio") ?? ParameterValues.Text(parameters, "aspect_ratio");
        if (task != "text-to-video")
        {
            if (raw is not null) throw new SkillException("invalid_parameter", "图生视频不接受 aspect_ratio，画面比例跟随首帧。");
            return null;
        }

        if (raw is null) return "16:9";
        if (raw is not ("16:9" or "9:16" or "1:1" or "4:3" or "3:4")) throw new SkillException("invalid_parameter", "aspect_ratio 仅支持 16:9、9:16、1:1、4:3 或 3:4。");
        invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("aspect_ratio_to_ratio")), ("source_parameters", Strings("/parameters/aspect_ratio")), ("target_parameters", Strings("/parameters/ratio"))));
        return raw;
    }

    private static bool ResolveBool(JsonObject? source, string name, Dictionary<string, string?> direct, string option, bool fallback)
    {
        if (direct.TryGetValue(option, out var text)) return text == "true" ? true : text == "false" ? false : throw new SkillException("invalid_parameter", option + " 必须为 true 或 false。");
        if (source?[name] is null) return fallback;
        if (source[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        throw new SkillException("invalid_parameter", name + " 必须为布尔值。");
    }

    private static int? ResolveSeed(JsonObject? source, Dictionary<string, string?> direct)
    {
        var text = direct.GetValueOrDefault("provider-aliyun-seed") ?? NumberText(source, "seed");
        if (text is null) return null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new SkillException("invalid_parameter", "seed 必须是 0 至 2147483647 的整数。");
        }

        return value;
    }

    private static string? ResolveAudio(JsonObject? source, Dictionary<string, string?> direct)
    {
        var value = direct.GetValueOrDefault("provider-aliyun-audio-url") ?? (source is null ? null : ParameterValues.Text(source, "audio_url"));
        if (value is null) return null;
        if (value.StartsWith("oss://", StringComparison.OrdinalIgnoreCase)) throw new SkillException("invalid_parameter", "本批不上传临时 OSS 音频，只接受公网 HTTPS URL。");
        if (!IsMediaUrl(value)) throw new SkillException("invalid_parameter", "音频 URL 必须是不含用户信息和片段的 HTTPS 地址。");
        return value;
    }

    private static string? NumberText(JsonObject? parameters, string name)
    {
        if (parameters?[name] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<string>(out var text)) return text;
        throw new SkillException("invalid_parameter", name + " 必须为整数。");
    }

    private static void RejectUnknown(JsonObject source, string[] allowed)
    {
        foreach (var pair in source)
        {
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new SkillException("invalid_parameter", "参数 " + pair.Key + " 不属于本批万相 2.7 视频契约。");
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
                case "duration": parameters[name] = N(ParseInteger(pair.Value, name)); break;
                case "resolution" or "aspect_ratio" or "negative_prompt": parameters[name] = S(pair.Value); break;
                case "provider_aliyun_prompt_extend": Aliyun(providers)["prompt_extend"] = B(pair.Value == "true"); break;
                case "provider_aliyun_watermark": Aliyun(providers)["watermark"] = B(pair.Value == "true"); break;
                case "provider_aliyun_seed": Aliyun(providers)["seed"] = N(ParseInteger(pair.Value, "seed")); break;
                case "provider_aliyun_audio_url": Aliyun(providers)["audio_url"] = S(pair.Value); break;
            }
        }

        input["parameters"] = parameters;
        input["execution"] = execution;
        if (providers.Count > 0) input["provider_parameters"] = providers;
        return input;
    }

    private static long ParseInteger(string? value, string name)
    {
        if (value is null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new SkillException("invalid_parameter", name + " 必须为整数。");
        }

        return number;
    }

    private static JsonObject Aliyun(JsonObject providers)
    {
        if (providers["aliyun"] is not JsonObject aliyun)
        {
            aliyun = new JsonObject();
            providers["aliyun"] = aliyun;
        }

        return aliyun;
    }
}
