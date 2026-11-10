using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Zhipu.Image.Metadata;

internal static class ZhipuImageMetadata
{
    public const string SkillId = "skill-zhipu-image";
    public const string DefaultModel = "cogview-4";
    public const string DefaultApiUrl = "https://open.bigmodel.cn/api";
    public const string DefaultSize = "1024x1024";
    public const string DefaultQuality = "standard";

    private static string Version => typeof(ZhipuImageMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("zhipu")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        var resourceName = typeof(ZhipuImageMetadata).Assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("request.schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("请求 Schema 资源缺失。");
        using var stream = typeof(ZhipuImageMetadata).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("请求 Schema 资源不可读。");
        var schema = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("请求 Schema 无效。");
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", ["--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--size", "--width", "--height", "--quality", "--provider-zhipu-watermark", "--provider-zhipu-user-id", "--timeout", "--try-run", "--verbose", "--no-wait", "--output-format"]));
        var conversions = new JsonArray(Obj(
            ("rule_id", S("dimensions_to_size")),
            ("source_parameters", Strings("/parameters/width", "/parameters/height")),
            ("target_parameters", Strings("/parameters/size"))));
        var task = Obj(
            ("id", S("text-to-image")), ("media_type", S("image")), ("parameters", Parameters()),
            ("parameter_conversions", conversions), ("validation_schema", ZhipuImageSchemas.Effective()),
            ("supports_partial", B(false)), ("supports_async", B(false)), ("task_context_required", B(false)),
            ("task_context_schema", null), ("attachment_limits", new JsonArray()));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("智谱 CogView 图片生成")), ("provider", S("zhipu")), ("version", S(Version)),
            ("capabilities", Strings("image")), ("default_api_url", S(DefaultApiUrl)),
            ("request_schema_version", S(ContractVersion)), ("request_schema", schema),
            ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", new JsonArray()),
            ("tasks", new JsonArray((JsonNode)task)),
            ("model_discovery", Obj(("source", S("unsupported")), ("command", null), ("path", null), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-image")), ("default_models", Obj(("text-to-image", S(DefaultModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(
                ("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                ("description", S("智谱 API Key，通过 Authorization Bearer 发送，不写入请求文件或响应。")),
                ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    private static JsonArray Parameters() => new(
        (JsonNode)Param("size", "--size", "string", DefaultSize, "CogView 图片尺寸，宽x高。"),
        (JsonNode)Param("width", "--width", "integer", null, "与 height 成对转换为 size。"),
        (JsonNode)Param("height", "--height", "integer", null, "与 width 成对转换为 size。"),
        (JsonNode)Param("quality", "--quality", "string", DefaultQuality, "CogView 图片质量。", ["standard", "hd"]),
        (JsonNode)Param("watermark_enabled", "--provider-zhipu-watermark", "boolean", null, "是否保留官方水印。", scope: "provider"),
        (JsonNode)Param("user_id", "--provider-zhipu-user-id", "string", null, "终端用户 ID，6 至 128 字符。", scope: "provider"));

    private static JsonObject Param(string name, string cli, string type, object? defaultValue, string description, string[]? values = null, string scope = "common")
    {
        var path = scope == "provider" ? "/provider_parameters/zhipu/" + name : "/parameters/" + name;
        var result = Obj(("name", S(name)), ("cli_name", S(cli)), ("request_path", S(path)), ("scope", S(scope)),
            ("type", S(type)), ("required", B(false)), ("description", S(description)), ("sensitive", B(false)));
        if (defaultValue is string text) result["default"] = S(text);
        if (values is not null) result["enum"] = Strings(values);
        return result;
    }

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            var normalized = option[2..].Replace('-', '_');
            var type = normalized is "timeout" or "width" or "height" ? "integer" : normalized is "try_run" or "verbose" or "no_wait" ? "boolean" : "string";
            parameters.Add(Obj(("name", S(normalized)), ("cli_name", S(option)), ("request_path", S("/" + normalized)),
                ("scope", S(normalized.StartsWith("provider_zhipu_", StringComparison.Ordinal) ? "provider" : "common")),
                ("type", S(type)), ("required", B(option == "--request-id" && name != "generate" || option == "--api-key" && auth)),
                ("description", S("智谱图片协议参数。")), ("sensitive", B(option == "--api-key"))));
        }

        return Obj(("name", S(name)), ("description", S(name == "generate" ? "提交 CogView 文生图并下载验证后的本地图片。" : "读取技能协议数据。")),
            ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
