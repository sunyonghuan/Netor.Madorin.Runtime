using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Metadata;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Video.Requests;

internal sealed class VideoGenerationOptionsFactory(WorkspacePaths paths)
{
    private static readonly HashSet<string> AllowedOptions = new([
        "request-id", "request-file", "api-key", "api-url", "prompt", "task", "model", "output-dir",
        "duration", "aspect-ratio", "resolution", "timeout", "try-run", "verbose", "no-wait", "output-format",
        "enable-audio", "fps", "image-url", "first-frame-url", "last-frame-url", "multi-shot",
        "width", "height", "size", "quality", "seed", "style", "negative-prompt", "num-images",
        "reference-weight", "guidance-scale", "steps"
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> Switches = new(["try-run", "verbose", "no-wait", "enable-audio", "multi-shot"], StringComparer.Ordinal);

    public async Task<VideoGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = VideoAttachments.Extract(args);
        var direct = Parse(extracted.Remaining, AllowedOptions, Switches);
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("timeout", out var directTimeout))
        {
            invocation.SetTimeout(ParsePositiveInt(directTimeout, 300, "timeout"));
        }

        var fileName = direct.GetValueOrDefault("request-file");
        VideoRequestFile? file = fileName is null ? null : await new GrokVideoRequestReader(paths, invocation).ReadAsync(
            fileName, invocation.CancellationToken, direct.TryGetValue("try-run", out var directTryRun) ? directTryRun == "true" : null);
        var fileId = file is null ? null : VideoParameterValues.Text(file.Content, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.TryGetValue("request-id", out var directId) && fileId is not null && directId != fileId)
        {
            throw new SkillException("invalid_parameter", "request_id 缺失、无效或与请求文件冲突。");
        }

        invocation.ResponseId = id;
        if (file is not null)
        {
            VideoAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory);
            if (!direct.ContainsKey("output-dir") && file.Content["execution"] is JsonObject controls && controls.ContainsKey("output_dir"))
            {
                paths.Resolve(VideoParameterValues.Text(controls, "output_dir")!, file.BaseDirectory);
            }
        }

        VideoAttachments.Validate(extracted.Attachments, extracted.Attachments is not null, paths, null);
        var input = VideoInputEcho.Build(direct, file);
        input["request_id"] = S(id);
        if (extracted.Attachments is not null)
        {
            input["attachments"] = extracted.Attachments;
        }

        var parameters = input["parameters"]!.AsObject();
        var execution = input["execution"]!.AsObject();
        invocation.TryRun = VideoParameterValues.Boolean(execution, "try_run");
        var timeout = VideoParameterValues.Integer(execution, "timeout", 300);
        invocation.SetTimeout(timeout);
        var tryRun = invocation.TryRun;
        var verbose = VideoParameterValues.Boolean(execution, "verbose");
        var noWait = VideoParameterValues.Boolean(execution, "no_wait");
        if (VideoParameterValues.Text(execution, "output_format", "json") != "json")
        {
            throw new SkillException("invalid_parameter", "标准输出格式仅支持 json。");
        }

        var task = VideoParameterValues.Text(input, "task", "text-to-video")!;
        var model = VideoParameterValues.Text(input, "model", GrokVideoMetadata.DefaultModel)!;
        invocation.Model = model;
        var prompt = VideoParameterValues.Text(input, "prompt");
        var duration = VideoParameterValues.Integer(parameters, "duration", 8);
        var aspectRatio = VideoParameterValues.Text(parameters, "aspect_ratio", "16:9")!;
        var resolution = VideoParameterValues.Text(parameters, "resolution", "480p")!;
        Validate(task, model, prompt, duration, aspectRatio, resolution);

        var apiKey = direct.GetValueOrDefault("api-key");
        var url = direct.GetValueOrDefault("api-url") ?? GrokVideoMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !TryEndpoint(url, out var endpoint))
        {
            throw new SkillException("invalid_parameter", "必须提供 api-key 及不含用户信息、查询参数或片段的 HTTPS API 地址；仅本机回环地址允许 HTTP。");
        }

        var output = VideoParameterValues.Text(execution, "output_dir");
        if (!tryRun && !noWait && string.IsNullOrWhiteSpace(output))
        {
            throw new SkillException("invalid_parameter", "等待落盘必须指定 output-dir。");
        }

        // argv 相对启动目录；只在请求文件中给出的路径相对该文件目录。
        var outputBase = direct.ContainsKey("output-dir") ? null : file?.BaseDirectory;
        var resolvedOutput = output is null ? null : paths.Resolve(output, outputBase);
        IgnoreUnsupported(input, parameters, invocation);
        invocation.ThrowIfCancellationRequested();
        return new VideoGenerationOptions(
            id!, task, model, prompt!, apiKey, endpoint!, resolvedOutput,
            duration, aspectRatio, resolution, timeout, tryRun, verbose, noWait, input);
    }

    internal static bool TryEndpoint(string url, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            !(parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback) ||
            parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static void IgnoreUnsupported(JsonObject input, JsonObject parameters, SkillInvocation invocation)
    {
        foreach (var name in parameters.Select(pair => pair.Key).ToArray())
        {
            if (name is not ("duration" or "aspect_ratio" or "resolution"))
            {
                Ignore(invocation, "/parameters/" + EscapePointer(name), "unsupported_parameter");
                parameters.Remove(name);
            }
        }

        if (input["provider_parameters"] is JsonObject providers)
        {
            foreach (var branch in providers.ToArray())
            {
                Ignore(invocation, "/provider_parameters/" + EscapePointer(branch.Key), branch.Key == "grok" ? "unsupported_parameter" : "other_provider");
                providers.Remove(branch.Key);
            }

            if (providers.Count == 0)
            {
                input.Remove("provider_parameters");
            }
        }

        if (input["attachments"] is JsonArray attachments)
        {
            for (var index = 0; index < attachments.Count; index++)
            {
                Ignore(invocation, "/attachments/" + index, "unsupported_attachment_role");
            }

            input["attachments"] = new JsonArray();
        }
    }

    private static void Validate(string task, string model, string? prompt, int duration, string aspectRatio, string resolution)
    {
        if (task != "text-to-video" || string.IsNullOrWhiteSpace(prompt) || !ModelIds.IsValid(model) ||
            duration is < 1 or > 15 ||
            !GrokVideoMetadata.SupportedAspectRatios.Contains(aspectRatio, StringComparer.Ordinal) ||
            !GrokVideoMetadata.SupportedResolutions.Contains(resolution, StringComparer.Ordinal))
        {
            throw new SkillException("invalid_parameter", "任务、模型、时长、宽高比、分辨率或提示词不合法。");
        }
    }

    private static void Ignore(SkillInvocation invocation, string pointer, string reason) =>
        invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S(pointer)), ("reason", S(reason))));
}
