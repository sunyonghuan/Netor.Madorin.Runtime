using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Bfl.Image.Metadata;

internal static class BflImageMetadata
{
    public const string SkillId = "skill-bfl-image";
    public const string DefaultModel = "flux-2-pro";
    public const string DefaultApiUrl = "https://api.bfl.ai";

    private static string Version => typeof(BflImageMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("bfl")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        var resourceName = typeof(BflImageMetadata).Assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("request.schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("请求 Schema 资源缺失。");
        using var stream = typeof(BflImageMetadata).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("请求 Schema 资源不可读。");
        var schema = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("请求 Schema 无效。");
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", ["--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--width", "--height", "--seed", "--safety-tolerance", "--image-format", "--provider-bfl-disable-pup", "--timeout", "--try-run", "--verbose", "--no-wait"]));
        var task = Obj(
            ("id", S("text-to-image")), ("media_type", S("image")), ("parameters", Parameters()),
            ("parameter_conversions", new JsonArray()), ("validation_schema", BflImageSchemas.Effective()),
            ("supports_partial", B(false)), ("supports_async", B(false)), ("task_context_required", B(false)),
            ("task_context_schema", null), ("attachment_limits", new JsonArray()));
        var authenticationParameter = Obj(
            ("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null),
            ("scope", S("common")), ("description", S("BFL API Key，通过 x-key 请求头发送，不写入请求文件或响应。")),
            ("type", S("string")), ("required", B(true)), ("sensitive", B(true)));
        var authentication = Obj(("type", S("api_key")), ("parameters", new JsonArray(authenticationParameter)));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("Black Forest Labs FLUX 图片生成")), ("provider", S("bfl")), ("version", S(Version)),
            ("capabilities", Strings("image")), ("default_api_url", S(DefaultApiUrl)),
            ("request_schema_version", S(ContractVersion)), ("request_schema", schema),
            ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", new JsonArray()),
            ("tasks", new JsonArray((JsonNode)task)), ("model_discovery", Obj(("source", S("unsupported")), ("command", null), ("path", null), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-image")), ("default_models", Obj(("text-to-image", S(DefaultModel)))),
            ("authentication", authentication), ("extensions", new JsonObject()));
    }

    private static JsonArray Parameters() => new(
        (JsonNode)Param("width", "--width", "integer", null, "输出宽度，256 至 6256；必须与 height 同时提供。"),
        (JsonNode)Param("height", "--height", "integer", null, "输出高度，256 至 6256；必须与 width 同时提供。"),
        (JsonNode)Param("seed", "--seed", "integer", null, "可选复现随机种子。"),
        (JsonNode)Param("safety_tolerance", "--safety-tolerance", "integer", 2, "FLUX.2 内容安全容忍度，0 至 5。"),
        (JsonNode)Param("output_format", "--image-format", "string", "jpeg", "输出图片格式。", ["jpeg", "png", "webp"]),
        (JsonNode)Param("disable_pup", "--provider-bfl-disable-pup", "boolean", false, "true 时禁用 FLUX.2 自动提示词优化。"));

    private static JsonObject Param(string name, string cli, string type, object? defaultValue, string description, string[]? values = null)
    {
        var result = Obj(("name", S(name)), ("cli_name", S(cli)), ("request_path", S("/parameters/" + name)),
            ("scope", S(name == "disable_pup" ? "provider" : "common")), ("type", S(type)),
            ("required", B(false)), ("description", S(description)), ("sensitive", B(false)));
        if (defaultValue is string text) result["default"] = S(text);
        else if (defaultValue is bool flag) result["default"] = B(flag);
        else if (defaultValue is int number) result["default"] = N(number);
        if (values is not null) result["enum"] = Strings(values);
        return result;
    }

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            var normalized = option[2..].Replace('-', '_');
            var path = normalized switch
            {
                "width" or "height" or "seed" or "safety_tolerance" or "image_format" => "/parameters/" + (normalized == "image_format" ? "output_format" : normalized),
                "provider_bfl_disable_pup" => "/parameters/disable_pup",
                "output_dir" or "timeout" or "try_run" or "verbose" or "no_wait" or "output_format" => "/execution/" + normalized,
                _ => "/" + normalized
            };
            var type = normalized is "timeout" or "width" or "height" or "seed" or "safety_tolerance" ? "integer" : normalized is "try_run" or "verbose" or "no_wait" or "provider_bfl_disable_pup" ? "boolean" : "string";
            parameters.Add(Obj(("name", S(normalized)), ("cli_name", S(option)), ("request_path", S(path)),
                ("scope", S(normalized.StartsWith("provider_bfl_", StringComparison.Ordinal) ? "provider" : "common")),
                ("type", S(type)), ("required", B(option == "--request-id" && name != "generate" || option == "--api-key" && auth)),
                ("description", S("BFL 协议参数。")), ("sensitive", B(option == "--api-key"))));
        }
        return Obj(("name", S(name)), ("description", S(name == "generate" ? "提交 FLUX.2 Pro 图片生成并在同一调用内轮询和落盘。" : "读取技能协议数据。")),
            ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
