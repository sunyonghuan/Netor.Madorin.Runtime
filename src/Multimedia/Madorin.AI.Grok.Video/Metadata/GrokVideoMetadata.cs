using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Grok.Video.Metadata;

internal static class GrokVideoMetadata
{
    public const string SkillId = "skill-grok-video";
    public const string DefaultModel = "grok-imagine-video-1.5";
    public const string DefaultApiUrl = "https://api.x.ai/v1";
    public static readonly string[] SupportedAspectRatios = ["1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "21:9", "5:2"];
    public static readonly string[] SupportedResolutions = ["480p", "720p", "1080p"];

    private static string Version => typeof(GrokVideoMetadata).Assembly.GetName().Version!.ToString(3);

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("grok")), ("capabilities", Strings("video")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        using var schemaStream = typeof(GrokVideoMetadata).Assembly.GetManifestResourceStream("Madorin.AI.Grok.Video.request.schema.json");
        if (schemaStream is null)
        {
            throw new InvalidOperationException("The embedded request schema is missing.");
        }

        var requestSchema = JsonNode.Parse(schemaStream)?.AsObject() ?? throw new InvalidOperationException("The embedded request schema is invalid.");
        var commands = new JsonArray(
            Command("metadata", "读取技能能力与契约。", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", "从目标 API 获取模型目录，任务能力未返回时标为未知。", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", "读取技能和契约版本。", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", "提交 Grok 文生视频；默认轮询完成后下载，--no-wait 只返回任务。", true, "GenerateResponse",
                ["--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt",
                 "--duration", "--aspect-ratio", "--resolution", "--output-format", "--timeout", "--try-run", "--verbose", "--no-wait",
                 "--enable-audio", "--fps", "--image-url", "--first-frame-url", "--last-frame-url", "--multi-shot", "--width", "--height",
                 "--size", "--quality", "--seed", "--style", "--negative-prompt", "--num-images", "--reference-weight", "--guidance-scale", "--steps"]),
            Command("query", "按任务 ID 查询一次视频状态；--wait 在总预算内轮询到终态，不下载。", true, "TaskResponse",
                ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--timeout", "--wait", "--output-format", "--verbose"]),
            Command("download", "下载已完成视频并校验 MP4 后落盘，不创建新任务。", true, "GenerateResponse",
                ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--output-dir", "--timeout", "--output-format", "--verbose"]));
        var task = Obj(
                ("id", S("text-to-video")), ("media_type", S("video")), ("parameters", VideoParameters()),
            ("parameter_conversions", new JsonArray()), ("validation_schema", GrokVideoSchemas.Effective()),
            ("supports_partial", B(false)), ("supports_async", B(true)), ("task_context_required", B(true)),
            ("task_context_schema", GrokVideoSchemas.TaskContext()), ("attachment_limits", new JsonArray()));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("Grok 视频生成")), ("provider", S("grok")), ("version", S(Version)), ("capabilities", Strings("video")),
            ("default_api_url", S(DefaultApiUrl)), ("request_schema_version", S(ContractVersion)), ("request_schema", requestSchema),
            ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray(task)),
            ("model_discovery", Obj(("source", S("api")), ("command", S("list-models")), ("path", S("models")), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-video")),
            ("default_models", Obj(("text-to-video", S(DefaultModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(
                Obj(("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                    ("description", S("xAI API Key，以 Bearer Token 直接传递，不落盘或回显。")),
                    ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    public static JsonObject TaskContext(string taskId, string model) => Obj(
        ("schema_version", S(ContractVersion)), ("skill_id", S(SkillId)), ("provider", S("grok")),
        ("task_id", S(taskId)), ("task", S("text-to-video")), ("model", S(model)),
        ("output_indices", new JsonArray(N(1))), ("provider_context", new JsonObject()));

    private static JsonArray VideoParameters() => new(
        Parameter("prompt", "--prompt", "string", true, null, "生成视频的文本提示词。", "/prompt"),
        WithBounds(Parameter("duration", "--duration", "integer", false, 8, "目标视频时长，单位秒，对应 Provider duration。"), 1, 15),
        WithEnum(Parameter("aspect_ratio", "--aspect-ratio", "string", false, "16:9", "视频画面宽高比。"), SupportedAspectRatios),
        WithEnum(Parameter("resolution", "--resolution", "string", false, "480p", "视频分辨率档位。"), SupportedResolutions));

    private static JsonObject Parameter(string name, string cliName, string type, bool required, object? defaultValue, string description, string? requestPath = null)
    {
        var parameter = Obj(
            ("name", S(name)), ("cli_name", S(cliName)), ("request_path", S(requestPath ?? "/parameters/" + name)),
            ("scope", S("common")), ("type", S(type)), ("required", B(required)), ("description", S(description)), ("sensitive", B(false)));
        if (defaultValue is not null)
        {
            parameter.Add("default", defaultValue is string text ? S(text) : N(Convert.ToInt64(defaultValue)));
        }

        return parameter;
    }

    private static JsonObject WithEnum(JsonObject parameter, params string[] values)
    {
        parameter["enum"] = Strings(values);
        return parameter;
    }

    private static JsonObject WithBounds(JsonObject parameter, int minimum, int maximum)
    {
        parameter["minimum"] = N(minimum);
        parameter["maximum"] = N(maximum);
        return parameter;
    }

    private static JsonObject Command(string name, string description, bool auth, string response, string[] optionNames)
    {
        var parameters = new JsonArray();
        foreach (var option in optionNames)
        {
            var field = option[2..].Replace('-', '_');
            var path = field switch
            {
                "request_id" or "task" or "model" or "prompt" => "/" + field,
                "output_dir" or "timeout" or "try_run" or "verbose" or "no_wait" or "output_format" => "/execution/" + field,
                "duration" or "aspect_ratio" or "resolution" => "/parameters/" + field,
                _ => null
            };
            var text = field switch
            {
                "request_id" => "主程序分配的调用关联 ID，响应 response_id 原样回传。",
                "request_file" => "统一生成请求 JSON 文件，路径相对启动工作目录。",
                "api_url" => "xAI API 根地址，默认 https://api.x.ai/v1。生成追加 videos/generations，查询与下载使用 videos/{task_id}。",
                "api_key" => "xAI API Key，通过 Bearer Token 认证，不回显。",
                "output_dir" => "产物目录，必须位于工作目录授权根内。仅提交时可省略。",
                "task" => "生成任务，当前仅支持 text-to-video。",
                "model" => "目标服务模型 ID，原样传递；本地不维护模型名称白名单。",
                "prompt" => "生成视频的提示词。",
                "duration" => "视频时长，单位秒，范围 1 至 15，默认 8。",
                "aspect_ratio" => "画面比例，支持 1:1、16:9、9:16、4:3、3:4、3:2、2:3、21:9、5:2，默认 16:9。",
                "resolution" => "分辨率档位，支持 480p、720p、1080p，默认 480p。",
                "timeout" => "从命令起点开始的调用总预算，单位秒，默认 300。",
                "try_run" => "仅本地校验并映射，不联网、不读媒体、不写文件。",
                "verbose" => "诊断控制；try-run 只回显，不改变 stdout 协议。",
                "no_wait" => "只提交异步任务并返回任务 ID，不等待完成、不下载。",
                "task_id" => "先前提交返回的远端视频任务 ID。",
                "task_context_file" => "查询和下载必需。宿主保存的任务上下文；恢复原模型与槽位，并与 task-id 及远端模型交叉校验。",
                "wait" => "在本次总预算内轮询到远端终态，不下载媒体。",
                _ => "标准输出协议格式，仅支持 json。"
            };
            var parameter = Obj(
                ("name", S(field)), ("cli_name", S(option)), ("request_path", S(path)), ("scope", S("common")),
                ("type", S(field is "timeout" or "duration" ? "integer" : field is "try_run" or "verbose" or "no_wait" or "wait" ? "boolean" : "string")),
                ("required", B(option == "--request-id" && name != "generate" || option == "--api-key" && auth || option is "--task-id" or "--task-context-file" || option == "--output-dir" && name == "download")),
                ("description", S(text)), ("sensitive", B(field == "api_key")));
            if (field == "output_format")
            {
                parameter["default"] = S("json");
                parameter["enum"] = Strings("json");
            }

            if (field == "timeout")
            {
                parameter["default"] = N(300);
                parameter["minimum"] = N(1);
                parameter["maximum"] = N(int.MaxValue);
            }

            if (field == "duration")
            {
                parameter["default"] = N(8);
                parameter["minimum"] = N(1);
                parameter["maximum"] = N(15);
            }

            if (field == "aspect_ratio")
            {
                parameter["default"] = S("16:9");
                parameter["enum"] = Strings(SupportedAspectRatios);
            }

            if (field == "resolution")
            {
                parameter["default"] = S("480p");
                parameter["enum"] = Strings(SupportedResolutions);
            }

            if (field is "try_run" or "verbose" or "no_wait" or "wait")
            {
                parameter["default"] = B(false);
            }

            parameters.Add((JsonNode)parameter);
        }

        return Obj(("name", S(name)), ("description", S(description)), ("parameters", parameters),
            ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
