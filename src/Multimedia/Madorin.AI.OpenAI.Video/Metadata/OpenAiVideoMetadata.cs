using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Video.Metadata;

internal static class OpenAiVideoMetadata
{
    public static readonly string[] Sizes = ["720x1280", "1280x720", "1024x1792", "1792x1024"];
    public static JsonObject Version(string? id) => Obj(("schema_version", S("1.0")), ("response_id", S(id)), ("skill_id", S("skill-openai-video")),
        ("version", S(typeof(OpenAiVideoMetadata).Assembly.GetName().Version!.ToString(3))), ("provider", S("openai")),
        ("capabilities", Strings("video")), ("runtime", S("native-aot")));

    public static JsonObject Build(string? id)
    {
        using var stream = typeof(OpenAiVideoMetadata).Assembly.GetManifestResourceStream("Madorin.AI.OpenAI.Video.request.schema.json")!;
        var schema = JsonNode.Parse(stream)!.AsObject();
        var tasks = new JsonArray();
        foreach (var taskId in new[] { "text-to-video", "image-to-video" })
        {
            var parameters = new JsonArray(Parameter("prompt", "/prompt", "string", true, "视频描述，保留完整 Unicode 文本。"),
                Parameter("size", "/parameters/size", "string", false, "像素宽x高，不自动缩放。"),
                Parameter("duration", "/parameters/duration", "integer", false, "生成时长，秒；精确映射 seconds 字符串。"),
                Parameter("num_videos", "/parameters/num_videos", "integer", false, "每次仅一个远端任务及一个视频。"));
            parameters[1]!["default"] = S("720x1280"); parameters[1]!["enum"] = Strings(Sizes);
            parameters[2]!["default"] = N(4); parameters[2]!["enum"] = new JsonArray(N(4), N(8), N(12));
            parameters[3]!["default"] = N(1); parameters[3]!["minimum"] = N(1); parameters[3]!["maximum"] = N(1);
            tasks.Add((JsonNode)Obj(("id", S(taskId)), ("media_type", S("video")), ("parameters", parameters),
                ("parameter_conversions", VideoSchemas.Conversions()), ("validation_schema", VideoSchemas.Effective(taskId, schema)),
                ("supports_partial", B(false)), ("supports_async", B(true)), ("task_context_required", B(true)), ("task_context_schema", VideoSchemas.Context()),
                ("attachment_limits", taskId == "text-to-video" ? new JsonArray() : new JsonArray(Obj(("role", S("reference")),
                    ("min_count", N(1)), ("max_count", N(1)), ("mime_types", Strings("image/png", "image/jpeg", "image/webp")),
                    ("max_bytes", N(20971520)), ("description", S("仅本地完整图片，宽高必须与视频相同。")))))));
        }
        var result = Version(id);
        result["name"] = S("OpenAI 视频生成");
        result["default_api_url"] = S("https://api.openai.com/v1");
        result["request_schema_version"] = S("1.0"); result["request_schema"] = schema;
        result["max_request_bytes"] = N(SkillProtocol.MaxRequestBytes);
        result["models"] = new JsonArray(); result["tasks"] = tasks;
        result["default_task"] = S("text-to-video"); result["default_models"] = new JsonObject();
        result["model_discovery"] = Obj(("source", S("api")), ("command", S("list-models")), ("path", S("models")), ("capability_information", S("unknown")));
        result["authentication"] = Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(("name", S("api_key")),
            ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")), ("type", S("string")), ("required", B(true)),
            ("sensitive", B(true)), ("description", S("Bearer Key，仅 argv，不落盘。"))))));
        result["commands"] = new JsonArray(Command("metadata", ["request-id", "output-format", "verbose"], false, "SkillMetadata"),
            Command("version", ["request-id", "output-format", "verbose"], false, "VersionInfo"),
            Command("list-models", ["request-id", "api-url", "timeout", "output-format", "verbose"], true, "ModelList"),
            Command("generate", VideoGenerationOptionsFactory.OptionNames.Where(name => name != "api-key").Concat(
                ["attachment", "reference-image", "image-url", "first-frame-url", "last-frame-url"]).ToArray(), true, "GenerateResponse|TryRunResponse|TaskResponse"),
            Command("query", ["request-id", "task-id", "task-context-file", "api-url", "wait", "timeout", "output-format", "verbose"], true, "TaskResponse"),
            Command("download", ["request-id", "task-id", "task-context-file", "api-url", "output-dir", "timeout", "output-format", "verbose"], true, "GenerateResponse"));
        result["extensions"] = Obj(("openai", Obj(("service_notice", S("官方 SDK 标记 Sora API 计划于 2026-09-24 关闭；兼容服务能力以实际端点为准。")),
            ("remote_cancel_supported", B(false)), ("max_download_bytes", N(536870912)), ("required_tools", Strings("ffprobe", "ffmpeg")))));
        return result;
    }

    private static JsonObject Parameter(string name, string? requestPath, string type, bool required, string description) =>
        Obj(("name", S(name)), ("cli_name", S("--" + name.Replace('_', '-'))), ("request_path", S(requestPath)), ("scope", S("common")),
            ("type", S(type)), ("required", B(required)), ("sensitive", B(false)), ("description", S(description)));

    private static JsonObject Command(string name, string[] options, bool auth, string response)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            var field = option.Replace('-', '_');
            var isBoolean = field is "try_run" or "verbose" or "no_wait" or "wait" or "audio" or "multi_shot";
            var isInteger = field is "timeout" or "duration" or "width" or "height" or "num_videos";
            var isAttachment = field is "attachment" or "reference_image" or "image_url" or "first_frame_url" or "last_frame_url";
            var pointer = field switch
            {
                "request_id" or "task" or "model" or "prompt" => "/" + field,
                "timeout" or "try_run" or "verbose" or "no_wait" or "output_dir" or "output_format" => "/execution/" + field,
                "api_url" or "request_file" or "task_id" or "task_context_file" or "wait" => null,
                _ => isAttachment ? "/attachments" : "/parameters/" + field
            };
            var description = field switch
            {
                "request_id" => "调用方关联 ID，原样回传 response_id。",
                "task_id" => "远端任务 ID，只允许字母数字及下划线、连字符，最长 512。",
                "task_context_file" => "原任务完整 UTF-8 上下文文件，恢复必须提供；不接受生成文件或整个响应。",
                "request_file" => "UTF-8 统一请求文件，最大 1 MiB。",
                "api_url" => "API 基础地址，追加 models/videos；HTTPS 或回环 HTTP，禁止凭证、查询和片段。",
                "model" => "必须显式提供的原始模型 ID，不查静态白名单，不自动换模型。",
                "output_dir" => "授权根内的本地输出目录；no-wait 可省略。",
                "no_wait" => "只提交一次，返回任务及完整上下文，不下载。",
                "wait" => "仅轮询既有任务至远端终态，不下载也不重提。",
                "try_run" => "共用正式映射器，只校验和回显，不联网、不读媒体、不写目录。",
                "verbose" => "诊断控制，try-run 只回显；不改变业务结果。",
                "timeout" => "命令起点的总预算，秒，所有阶段共用。",
                "output_format" => "stdout JSON 协议格式，不是视频编码。",
                "size" => "目标像素宽x高，默认 720x1280。",
                "duration" => "视频时长秒数，仅 4、8、12，默认 4。",
                "width" or "height" => "像素整数，成对提供并等价映射 size。",
                "num_videos" => "仅接受 1，不裁减请求数量。",
                "prompt" => "完整长提示词，保留中文。",
                "task" => "text-to-video 或 image-to-video，默认前者。",
                _ => isAttachment ? "可重复的附件入口；仅单张本地 reference 或 first_frame 等价转换，其他角色记录过滤。" : "公共兼容参数；本协议无可靠等价映射时记录过滤。"
            };
            var item = Parameter(field, pointer, isBoolean ? "boolean" : isInteger ? "integer" : isAttachment ? "array" : "string",
                name != "generate" && field is "request_id" or "task_id" or "task_context_file" || name == "download" && field == "output_dir", description);
            if (isBoolean) { item["default"] = B(false); }
            if (isInteger) { item["minimum"] = N(1); item["maximum"] = N(int.MaxValue); }
            if (field == "timeout") { item["default"] = N(300); }
            if (field == "output_format") { item["default"] = S("json"); item["enum"] = Strings("json"); }
            if (isAttachment) { item["repeatable"] = B(true); item["schema"] = Obj(("type", S("array")), ("items", field == "attachment" ? Obj(("type", S("object"))) : VideoSchemas.Text())); }
            parameters.Add((JsonNode)item);
        }
        if (auth)
        {
            var credential = Parameter("api_key", null, "string", true, "Bearer API Key，仅通过 argv 传入。详情见 authentication。");
            credential["sensitive"] = B(true);
            parameters.Add((JsonNode)credential);
        }
        return Obj(("name", S(name)), ("description", S("OpenAI 视频协议 " + name)), ("parameters", parameters),
            ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
