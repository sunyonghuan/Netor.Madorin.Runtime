using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.OpenAI.Image.Metadata;

internal static class OpenAiImageMetadata
{
    public const string SkillId = "skill-openai-image";
    public static readonly string[] SupportedSizes = ["auto", "1024x1024", "1536x1024", "1024x1536"];
    public static readonly string[] SupportedQualities = ["auto", "low", "medium", "high"];
    private static string Version => typeof(OpenAiImageMetadata).Assembly.GetName().Version!.ToString(3);

    public static JsonObject BuildVersion(string? id) => Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("openai")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        using var schemaStream = typeof(OpenAiImageMetadata).Assembly.GetManifestResourceStream("Madorin.AI.OpenAI.Image.request.schema.json");
        if (schemaStream is null)
        {
            throw new InvalidOperationException("The embedded request schema is missing.");
        }

        var requestSchema = JsonNode.Parse(schemaStream)?.AsObject() ?? throw new InvalidOperationException("The embedded request schema is invalid.");
        var commands = new JsonArray(Command("metadata", "读取技能能力与契约。", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", "从目标 API 获取模型目录，任务能力未返回时标为未知。", true, "ModelList", ["--request-id", "--api-url", "--timeout", "--output-format", "--verbose"]),
            Command("version", "读取技能和契约版本。", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", "生成图片并保存产物。", true, "GenerateResponse", ["--request-id", "--request-file", "--api-url", "--output-dir", "--task", "--model", "--output-format", "--timeout", "--try-run", "--verbose", "--no-wait"]));
        var generateParameters = commands[3]!["parameters"]!.AsArray();
        foreach (var option in new[] { "--attachment", "--reference-image", "--image-url", "--first-frame-url", "--last-frame-url" })
        {
            var schema = option == "--attachment" ? requestSchema["properties"]!["attachments"]!.DeepClone().AsObject() :
                Obj(("type", S("array")), ("items", Obj(("type", S("string")), ("minLength", N(1)),
                    ("description", S("本地路径或 HTTPS 媒体 URI；临时签名 URI 按敏感内容处理。")))));
            generateParameters.Add((JsonNode)Obj(("name", S(option[2..].Replace('-', '_'))), ("cli_name", S(option)),
                ("request_path", S("/attachments")), ("scope", S("common")), ("type", S("array")), ("required", B(false)),
                ("repeatable", B(true)), ("sensitive", B(false)), ("schema", schema),
                ("description", S("统一附件解析入口，可重复并保留顺序；对象入口与简写不能混用。当前文生图任务不使用附件，校验后按原索引记录忽略项。"))));
        }
        var task = Obj(("id", S("text-to-image")), ("media_type", S("image")), ("parameters", ImageParameters()),
                ("parameter_conversions", new JsonArray(OpenAiImageSchemas.DimensionsConversion(), OpenAiImageSchemas.FormatConversion())), ("validation_schema", OpenAiImageSchemas.Effective()),
                ("supports_partial", B(false)), ("supports_async", B(false)), ("task_context_required", B(false)),
                ("task_context_schema", null), ("attachment_limits", new JsonArray()));
        return Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("OpenAI 图片生成")), ("provider", S("openai")), ("version", S(Version)), ("capabilities", Strings("image")),
            ("default_api_url", S("https://api.openai.com/v1")), ("request_schema_version", S(ContractVersion)),
            ("request_schema", requestSchema),
            ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray(task)),
            ("model_discovery", Obj(("source", S("api")), ("command", S("list-models")), ("path", S("models")), ("capability_information", S("unknown")))), ("default_task", S("text-to-image")),
            ("default_models", Obj(("text-to-image", S("gpt-image-1")))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(("name", S("api_key")), ("cli_name", S("--api-key")),
                ("request_path", null), ("scope", S("common")), ("description", S("OpenAI Bearer API Key，仅直接传参，不落盘或回显。")),
                ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    private static JsonArray ImageParameters() => new(
        Parameter("prompt", "--prompt", "string", true, null, "OpenAI 图片生成提示词。", "/prompt"),
        WithEnum(Parameter("size", "--size", "string", false, "auto", "期望的图片尺寸，像素宽x高或 auto。"), SupportedSizes),
        WithEnum(Parameter("quality", "--quality", "string", false, "auto", "生成质量档位。"), SupportedQualities),
        WithBounds(Parameter("num_images", "--num-images", "integer", false, 1, "生成图片数量，对应 Provider n。")),
        WithEnum(Parameter("output_format", "--provider-openai-output-format", "string", false, "png", "OpenAI 产物编码格式，对应 Provider output_format。", "/provider_parameters/openai/output_format", "provider"), "png", "jpeg", "webp"));

    private static JsonObject Parameter(string name, string cliName, string type, bool required, object? defaultValue, string description, string? requestPath = null, string scope = "common")
    {
        var parameter = Obj(("name", S(name)), ("cli_name", S(cliName)), ("request_path", S(requestPath ?? "/parameters/" + name)),
            ("scope", S(scope)), ("type", S(type)), ("required", B(required)), ("description", S(description)), ("sensitive", B(false)));
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

    private static JsonObject WithBounds(JsonObject parameter)
    {
        parameter["minimum"] = N(1);
        parameter["maximum"] = N(10);
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
                "request_id" or "task" or "model" => "/" + field,
                "output_dir" or "timeout" or "try_run" or "verbose" or "no_wait" or "output_format" => "/execution/" + field,
                _ => null
            };
            var parameterDescription = field switch
            {
                "request_id" => "主程序调用关联 ID，原样回传 response_id。",
                "request_file" => "统一生成请求 JSON 文件，路径相对启动工作目录。",
                "api_url" => "HTTPS API 基础地址；生成追加 images/generations，模型查询追加 models；仅本机回环地址（如 localhost、127.0.0.1、[::1]）允许 HTTP，不接受用户信息、查询参数或片段。",
                "output_dir" => "产物目录，必须位于工作目录授权根内。",
                "task" => "生成任务，默认 text-to-image。",
                "model" => "目标服务模型 ID，原样传递；本地不维护模型名称白名单。",
                "timeout" => "从命令起点计算的总预算，单位秒。",
                "try_run" => "仅本地校验和映射，不联网、不写产物。",
                "verbose" => "诊断控制，试运行只回显值。",
                "no_wait" => "异步提交开关，本同步技能仅接受 false。",
                _ => "标准输出协议格式，仅接受 json。"
            };
            var parameter = Obj(("name", S(field)), ("cli_name", S(option)), ("request_path", S(path)), ("scope", S("common")),
                ("type", S(field == "timeout" ? "integer" : field is "try_run" or "verbose" or "no_wait" ? "boolean" : "string")),
                ("required", B(option == "--request-id" && name != "generate")), ("description", S(parameterDescription)), ("sensitive", B(false)));
            if (field == "output_format") { parameter["default"] = S("json"); parameter["enum"] = Strings("json"); }
            if (field == "timeout") { parameter["default"] = N(300); parameter["minimum"] = N(1); parameter["maximum"] = N(int.MaxValue); }
            if (field is "try_run" or "verbose" or "no_wait") { parameter["default"] = B(false); }
            parameters.Add((JsonNode)parameter);
        }

        return Obj(("name", S(name)), ("description", S(description)), ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }

}
