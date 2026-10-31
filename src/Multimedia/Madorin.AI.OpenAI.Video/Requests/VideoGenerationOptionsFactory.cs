using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.ParameterValues;

namespace Madorin.AI.OpenAI.Video.Requests;

internal sealed class VideoGenerationOptionsFactory(WorkspacePaths paths)
{
    internal static readonly string[] OptionNames = ["request-id", "request-file", "api-url", "api-key", "task", "model", "prompt",
        "output-dir", "size", "width", "height", "duration", "num-videos", "timeout", "try-run", "verbose", "no-wait", "output-format",
        "aspect-ratio", "fps", "seed", "negative-prompt", "quality", "style", "multi-shot", "audio"];

    public async Task<VideoGenerationOptions> CreateAsync(string[] args, SkillInvocation invocation)
    {
        var extracted = RequestAttachments.Extract(args);
        var direct = CommandLineParser.Parse(extracted.Remaining, new HashSet<string>(OptionNames, StringComparer.Ordinal),
            new HashSet<string>(["try-run", "verbose", "no-wait", "multi-shot", "audio"], StringComparer.Ordinal));
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
        if (direct.TryGetValue("try-run", out var mode)) { invocation.TryRun = mode == "true"; }
        if (direct.TryGetValue("timeout", out var budget)) { invocation.SetTimeout(CommandLineParser.ParsePositiveInt(budget, 300, "timeout")); }
        GenerationRequestFile? file = null;
        if (direct.TryGetValue("request-file", out var fileName))
        {
            file = await new GenerationRequestReader(paths, invocation).ReadAsync(fileName!, invocation.CancellationToken,
                direct.ContainsKey("try-run") ? mode == "true" : null);
        }
        var fileId = file is null ? null : Text(file.Content, "request_id");
        var id = direct.GetValueOrDefault("request-id") ?? fileId;
        if (!RequestIds.IsValidRequestId(id) || direct.ContainsKey("request-id") && fileId is not null && id != fileId)
        {
            throw Invalid("request_id 缺失、无效或与文件冲突。");
        }
        invocation.ResponseId = id;
        if (file is not null)
        {
            RequestAttachments.Validate(file.Content["attachments"], file.Content.ContainsKey("attachments"), paths, file.BaseDirectory);
            if (file.Content["execution"] is JsonObject controls && !direct.ContainsKey("timeout"))
            {
                invocation.SetTimeout(PositiveNumber(controls, "timeout", 300));
            }
        }
        if (extracted.Attachments is not null) { RequestAttachments.Validate(extracted.Attachments, true, paths, null); }
        var input = VideoInputEcho.Build(direct, file);
        if (extracted.Attachments is not null) { input["attachments"] = extracted.Attachments; }
        var parameters = input["parameters"]!.AsObject();
        var execution = input["execution"]!.AsObject();
        invocation.TryRun = Boolean(execution, "try_run");
        var task = Text(input, "task", "text-to-video")!;
        var model = Text(input, "model");
        invocation.Model = model;
        var prompt = Text(input, "prompt");
        if (task is not ("text-to-video" or "image-to-video") || !ModelIds.IsValid(model) || string.IsNullOrWhiteSpace(prompt))
        {
            throw Invalid("须提供支持的 task、非空 model 和 prompt；视频不设置默认模型。");
        }
        var size = ConvertDimensions(parameters, invocation);
        var duration = PositiveNumber(parameters, "duration", 4);
        var count = PositiveNumber(parameters, "num_videos", 1);
        if (!new[] { "720x1280", "1280x720", "1024x1792", "1792x1024" }.Contains(size, StringComparer.Ordinal) ||
            duration is not (4 or 8 or 12) || count != 1)
        {
            throw Invalid("size 不属于协议尺寸、duration 只能为 4/8/12 秒且 num_videos 只能为 1。");
        }
        var noWait = Boolean(execution, "no_wait");
        var verbose = Boolean(execution, "verbose");
        invocation.Verbose = verbose;
        var timeout = PositiveNumber(execution, "timeout", 300);
        invocation.SetTimeout(timeout);
        if (Text(execution, "output_format", "json") != "json") { throw Invalid("output-format 仅支持 json。"); }
        var output = Text(execution, "output_dir");
        if (output is not null && string.IsNullOrWhiteSpace(output)) { throw Invalid("output-dir 不得为空。"); }
        if (!invocation.TryRun && !noWait && output is null) { throw Invalid("等待生成须提供 output-dir。"); }
        var resolvedOutput = output is null ? null : paths.Resolve(output, direct.ContainsKey("output-dir") ? null : file?.BaseDirectory);
        var connection = ModelListOptions.Parse(ConnectionArgs(direct, id!, timeout), "https://api.openai.com/v1", invocation,
            requireApiKey: !invocation.TryRun);
        invocation.Verbose = verbose;
        var reference = SelectAttachment(input, task, extracted.Attachments is not null ? null : file?.BaseDirectory, invocation);
        Filter(input, parameters, invocation);
        invocation.ThrowIfCancellationRequested();
        return new(id!, task, model!, prompt!, connection.ApiKey, connection.Endpoint, resolvedOutput, size, duration, timeout,
            invocation.TryRun, verbose, noWait, reference, input);
    }

    private static string[] ConnectionArgs(Dictionary<string, string?> direct, string id, int timeout)
    {
        var result = new List<string> { "--request-id", id, "--timeout", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var name in new[] { "api-url", "api-key" })
        {
            if (direct.TryGetValue(name, out var value)) { result.Add("--" + name); result.Add(value!); }
        }
        return result.ToArray();
    }

    private static string ConvertDimensions(JsonObject parameters, SkillInvocation invocation)
    {
        var size = Text(parameters, "size");
        if (parameters.ContainsKey("width") || parameters.ContainsKey("height"))
        {
            var width = PositiveNumber(parameters, "width", 0);
            var height = PositiveNumber(parameters, "height", 0);
            if (width == 0 || height == 0) { throw Invalid("width/height 须成对提供。"); }
            var converted = FormattableString.Invariant($"{width}x{height}");
            if (size is not null && size != converted) { throw Invalid("width/height 与 size 冲突。"); }
            size = converted;
            invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("dimensions_to_size")),
                ("source_parameters", Strings("/parameters/width", "/parameters/height")), ("target_parameters", Strings("/parameters/size"))));
        }
        return size ?? "720x1280";
    }

    private string? SelectAttachment(JsonObject input, string task, string? baseDirectory, SkillInvocation invocation)
    {
        if (input["attachments"] is not JsonArray items)
        {
            if (task == "image-to-video") { throw Invalid("image-to-video 必须提供一张本地 reference 附件。"); }
            return null;
        }
        var references = new List<JsonObject>();
        foreach (var (item, index) in items.Select((item, index) => (item!.AsObject(), index)))
        {
            var role = Text(item, "role");
            if (role == "first_frame")
            {
                invocation.ConvertedParameters.Add((JsonNode)Obj(("rule_id", S("first_frame_to_reference")),
                    ("source_parameters", Strings($"/attachments/{index}/role")), ("target_parameters", Strings($"/attachments/{index}/role"))));
                role = "reference";
            }
            if (role == "reference") { references.Add(item); }
            else { invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S($"/attachments/{index}")), ("reason", S("unsupported_attachment_role")))); }
        }
        if (task == "text-to-video" && references.Count != 0 || task == "image-to-video" && references.Count != 1)
        {
            throw Invalid("text-to-video 不接受参考图；image-to-video 恰好接受一张参考图，不裁减多张附件。");
        }
        if (references.Count == 0) { return null; }
        var source = references[0]["source"]!.AsObject();
        if (!source.ContainsKey("path")) { throw Invalid("本批 input_reference 仅支持本地图片路径，不下载远端附件。"); }
        return paths.Resolve(Text(source, "path")!, baseDirectory);
    }

    private static void Filter(JsonObject input, JsonObject parameters, SkillInvocation invocation)
    {
        foreach (var name in parameters.Select(pair => pair.Key).ToArray())
        {
            if (name is "size" or "width" or "height" or "duration" or "num_videos") { continue; }
            invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S("/parameters/" + EscapePointer(name))), ("reason", S("unsupported_parameter"))));
            parameters.Remove(name);
        }
        if (input["provider_parameters"] is not JsonObject providers) { return; }
        foreach (var branch in providers)
        {
            foreach (var value in branch.Value!.AsObject())
            {
                invocation.IgnoredParameters.Add((JsonNode)Obj(("parameter", S("/provider_parameters/" + EscapePointer(branch.Key) + "/" + EscapePointer(value.Key))),
                    ("reason", S(branch.Key == "openai" ? "unsupported_parameter" : "other_provider"))));
            }
        }
        input.Remove("provider_parameters");
    }

    private static SkillException Invalid(string message) => new("invalid_parameter", message);
}
