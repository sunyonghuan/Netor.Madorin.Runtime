using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Aliyun.Image.Metadata;

internal static class AliyunImageMetadata
{
    public const string SkillId = "skill-aliyun-image";
    public const string DefaultModel = "wan2.6-t2i";
    public const string DefaultApiUrl = "https://dashscope.aliyuncs.com/api/v1";
    public static readonly string[] SupportedSizes = ["1280*1280", "1696*960", "960*1696", "1472*1104", "1104*1472", "768*2700"];
    private static string Version => typeof(AliyunImageMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)), ("version", S(Version)), ("provider", S("aliyun")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        using var stream = typeof(AliyunImageMetadata).Assembly.GetManifestResourceStream("Madorin.AI.Aliyun.Image.request.schema.json") ?? throw new InvalidOperationException("请求 Schema 资源缺失。");
        var schema = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("请求 Schema 无效。");
        var task = Obj(("id", S("text-to-image")), ("media_type", S("image")), ("parameters", Parameters()), ("parameter_conversions", new JsonArray()), ("validation_schema", AliyunImageSchemas.Effective()), ("supports_partial", B(false)), ("supports_async", B(false)), ("task_context_required", B(false)), ("task_context_schema", null), ("attachment_limits", new JsonArray()));
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", ["--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--size", "--num-images", "--negative-prompt", "--provider-aliyun-prompt-extend", "--provider-aliyun-watermark", "--provider-aliyun-seed", "--output-format", "--timeout", "--try-run", "--verbose", "--no-wait"]));
        var authentication = Obj(("type", S("api_key")), ("parameters", new JsonArray((JsonNode)Obj(("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")), ("description", S("阿里云百炼 API Key，通过 Authorization Bearer 认证，不落盘或回显。")), ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))));
        return Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)), ("name", S("阿里云百炼文生图")), ("provider", S("aliyun")), ("version", S(Version)), ("capabilities", Strings("image")), ("default_api_url", S(DefaultApiUrl)), ("request_schema_version", S(ContractVersion)), ("request_schema", schema), ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray((JsonNode)task)), ("model_discovery", Obj(("source", S("api")), ("command", S("list-models")), ("path", S("models")), ("capability_information", S("provider")))), ("default_task", S("text-to-image")), ("default_models", Obj(("text-to-image", S(DefaultModel)))), ("authentication", authentication), ("extensions", new JsonObject()));
    }

    private static JsonArray Parameters() => new(
        (JsonNode)Parameter("size", "--size", "string", "输出尺寸，使用宽*高格式。", "1280*1280", SupportedSizes),
        (JsonNode)Parameter("num_images", "--num-images", "integer", "生成数量，范围 1 至 4。", 1, null, 1, 4),
        (JsonNode)Parameter("negative_prompt", "--negative-prompt", "string", "反向提示词。", null),
        (JsonNode)Parameter("prompt_extend", "--provider-aliyun-prompt-extend", "boolean", "是否开启提示词智能改写。", true),
        (JsonNode)Parameter("watermark", "--provider-aliyun-watermark", "boolean", "是否添加水印。", false),
        (JsonNode)Parameter("seed", "--provider-aliyun-seed", "integer", "随机数种子，范围 0 至 2147483647。", null, null, 0, int.MaxValue));

    private static JsonObject Parameter(string name, string cli, string type, string description, object? defaultValue, string[]? values = null, int? minimum = null, int? maximum = null)
    {
        var parameter = Obj(("name", S(name)), ("cli_name", S(cli)), ("request_path", S("/parameters/" + name)), ("scope", S(name is "prompt_extend" or "watermark" or "seed" ? "provider" : "common")), ("type", S(type)), ("required", B(name == "prompt")), ("description", S(description)), ("sensitive", B(false)));
        if (defaultValue is string text) parameter["default"] = S(text); else if (defaultValue is int number) parameter["default"] = N(number); else if (defaultValue is bool boolean) parameter["default"] = B(boolean);
        if (values is not null) parameter["enum"] = Strings(values);
        if (minimum is not null) parameter["minimum"] = N(minimum.Value);
        if (maximum is not null) parameter["maximum"] = N(maximum.Value);
        return parameter;
    }

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            var field = option[2..].Replace('-', '_');
            var type = field is "timeout" or "num_images" or "provider_aliyun_seed" ? "integer" :
                field is "try_run" or "verbose" or "no_wait" or "provider_aliyun_prompt_extend" or "provider_aliyun_watermark" ? "boolean" : "string";
            var path = field switch
            {
                "prompt" or "task" or "model" => "/" + field,
                "size" or "num_images" or "negative_prompt" => "/parameters/" + field,
                "provider_aliyun_prompt_extend" => "/parameters/prompt_extend",
                "provider_aliyun_watermark" => "/parameters/watermark",
                "provider_aliyun_seed" => "/parameters/seed",
                "output_dir" or "timeout" or "try_run" or "verbose" or "no_wait" => "/execution/" + field,
                _ => null
            };
            parameters.Add(Obj(("name", S(field)), ("cli_name", S(option)), ("request_path", S(path)),
                ("scope", S(field.StartsWith("provider_aliyun_", StringComparison.Ordinal) ? "provider" : "common")),
                ("type", S(type)), ("required", B(option == "--api-key" && auth || option == "--request-id")),
                ("description", S("协议参数。")), ("sensitive", B(option == "--api-key"))));
        }
        return Obj(("name", S(name)), ("description", S(name == "generate" ? "通过阿里云百炼 Wan 2.6 文生图并保存 PNG 产物。" : "读取阿里云百炼技能协议数据。")), ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
