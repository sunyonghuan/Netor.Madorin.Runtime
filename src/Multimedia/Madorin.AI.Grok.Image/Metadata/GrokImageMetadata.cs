using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Grok.Image.Metadata;

internal static class GrokImageMetadata
{
    public const string SkillId = "skill-grok-image";
    public const string DefaultModel = "grok-imagine-image-2.0";
    public const string DefaultApiUrl = "https://api.x.ai/v1";
    public static readonly string[] SupportedModels = [DefaultModel];
    public static readonly string[] SupportedAspectRatios =
        ["auto", "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "2:1", "1:2", "19.5:9", "9:19.5", "20:9", "9:20", "21:9", "5:2"];

    private static string Version => typeof(GrokImageMetadata).Assembly.GetName().Version!.ToString(3);

    public static JsonObject BuildVersion(string? id) => Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("grok")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        using var schemaStream = typeof(GrokImageMetadata).Assembly.GetManifestResourceStream("Madorin.AI.Grok.Image.request.schema.json");
        if (schemaStream is null) { throw new InvalidOperationException("The embedded request schema is missing."); }
        var requestSchema = JsonNode.Parse(schemaStream)?.AsObject() ?? throw new InvalidOperationException("The embedded request schema is invalid.");
        var commands = new JsonArray(
            Command("metadata", "读取技能能力与契约。", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", "列出本地声明支持的图片模型。", false, "ModelList", ["--request-id", "--output-format", "--verbose"]),
            Command("version", "读取技能和契约版本。", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", "通过 Grok Imagine 生成图片并保存产物。", true, "GenerateResponse",
                ["--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--aspect-ratio", "--num-images", "--output-format", "--timeout", "--try-run", "--verbose", "--no-wait"]));
        var models = new JsonArray();
        foreach (var model in SupportedModels)
        {
            var task = Obj(("id", S("text-to-image")), ("media_type", S("image")), ("parameters", ImageParameters()),
                ("parameter_conversions", new JsonArray()), ("validation_schema", GrokImageSchemas.Effective(model)),
                ("supports_partial", B(false)), ("supports_async", B(false)), ("task_context_required", B(false)),
                ("task_context_schema", null), ("attachment_limits", new JsonArray()));
            models.Add((JsonNode)Obj(("id", S(model)), ("name", S(model)), ("tasks", new JsonArray(task))));
        }
        return Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("Grok 图片生成")), ("provider", S("grok")), ("version", S(Version)), ("capabilities", Strings("image")),
            ("default_api_url", S(DefaultApiUrl)), ("request_schema_version", S(ContractVersion)), ("request_schema", requestSchema),
            ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", models), ("default_task", S("text-to-image")),
            ("default_models", Obj(("text-to-image", S(DefaultModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(
                Obj(("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                    ("description", S("xAI API Key，以 Bearer Token 直接传递，不落盘或回显。")),
                    ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    public static JsonObject BuildModels(string? id) => Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)),
        ("provider", S("grok")), ("models", BuildMetadata(id)["models"]!.DeepClone()));

    private static JsonArray ImageParameters() => new(
        Parameter("prompt", "--prompt", "string", true, null, "生成图片的文本提示词。", "/prompt"),
        WithEnum(Parameter("aspect_ratio", "--aspect-ratio", "string", false, "auto", "图片输出宽高比。"), SupportedAspectRatios),
        WithBounds(Parameter("num_images", "--num-images", "integer", false, 1, "每次请求生成的图片数量，对应 Provider n。"), 1, 10));

    private static JsonObject Parameter(string name, string cliName, string type, bool required, object? defaultValue, string description, string? requestPath = null)
    {
        var parameter = Obj(("name", S(name)), ("cli_name", S(cliName)), ("request_path", S(requestPath ?? "/parameters/" + name)),
            ("scope", S("common")), ("type", S(type)), ("required", B(required)), ("description", S(description)), ("sensitive", B(false)));
        if (defaultValue is not null) { parameter.Add("default", defaultValue is string text ? S(text) : N(Convert.ToInt64(defaultValue))); }
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
                "aspect_ratio" or "num_images" => "/parameters/" + field,
                _ => null
            };
            var text = field switch
            {
                "request_id" => "主程序分配的调用关联 ID，响应 response_id 原样回传。",
                "request_file" => "统一生成请求 JSON 文件，路径相对启动工作目录。",
                "api_url" => "xAI API 根地址，默认 https://api.x.ai/v1，程序追加 images/generations。",
                "api_key" => "xAI API Key，通过 Bearer Token 认证，不回显。",
                "output_dir" => "产物目录，必须位于工作目录授权根内。",
                "task" => "生成任务，当前仅支持 text-to-image。",
                "model" => "当前支持的 Grok Imagine 图片模型 ID。",
                "prompt" => "生成图片的提示词。",
                "aspect_ratio" => "图片比例，支持 auto、1:1、16:9、9:16、4:3、3:4、3:2、2:3、2:1、1:2、19.5:9、9:19.5、20:9、9:20、21:9、5:2。",
                "num_images" => "生成数量，范围 1 至 10。",
                "timeout" => "从命令起点开始的调用总预算，单位秒。",
                "try_run" => "仅本地校验并映射，不联网、不读媒体、不写文件。",
                "verbose" => "诊断控制；try-run 只回显，不改变 stdout 协议。",
                "no_wait" => "异步提交开关；当前同步技能只接受 false。",
                _ => "标准输出协议格式，仅支持 json。"
            };
            var parameter = Obj(("name", S(field)), ("cli_name", S(option)), ("request_path", S(path)), ("scope", S("common")),
                ("type", S(field is "timeout" or "num_images" ? "integer" : field is "try_run" or "verbose" or "no_wait" ? "boolean" : "string")),
                ("required", B(option == "--request-id" && name != "generate" || option == "--api-key" && auth)),
                ("description", S(text)), ("sensitive", B(field == "api_key")));
            if (field == "output_format") { parameter["default"] = S("json"); parameter["enum"] = Strings("json"); }
            if (field == "timeout") { parameter["default"] = N(300); parameter["minimum"] = N(1); parameter["maximum"] = N(int.MaxValue); }
            if (field == "num_images") { parameter["default"] = N(1); parameter["minimum"] = N(1); parameter["maximum"] = N(10); }
            if (field == "aspect_ratio") { parameter["default"] = S("auto"); parameter["enum"] = Strings(SupportedAspectRatios); }
            if (field is "try_run" or "verbose" or "no_wait") { parameter["default"] = B(false); }
            parameters.Add((JsonNode)parameter);
        }
        return Obj(("name", S(name)), ("description", S(description)), ("parameters", parameters),
            ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
