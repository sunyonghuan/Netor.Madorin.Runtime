using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Gemini.Image.Metadata;

internal static class GeminiImageMetadata
{
    public const string SkillId = "skill-gemini-image";
    public const string DefaultModel = "gemini-2.0-flash-exp";
    public const string DefaultApiUrl = "https://generativelanguage.googleapis.com/v1beta";
    public static readonly string[] SupportedAspectRatios = ["auto", "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "2:1", "1:2", "19.5:9", "9:19.5", "20:9", "9:20", "21:9", "5:2"];
    private static string Version => typeof(GeminiImageMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)), ("version", S(Version)), ("provider", S("gemini")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject BuildMetadata(string? id)
    {
        using var stream = typeof(GeminiImageMetadata).Assembly.GetManifestResourceStream("Madorin.AI.Gemini.Image.request.schema.json") ?? throw new InvalidOperationException("请求 Schema 资源缺失。");
        var schema = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("请求 Schema 无效。");
        var task = Obj(("id", S("text-to-image")), ("media_type", S("image")), ("parameters", Parameters()), ("parameter_conversions", new JsonArray()), ("validation_schema", GeminiImageSchemas.Effective()), ("supports_partial", B(false)), ("supports_async", B(false)), ("task_context_required", B(false)), ("task_context_schema", null), ("attachment_limits", new JsonArray()));
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", ["--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--aspect-ratio", "--num-images", "--output-format", "--timeout", "--try-run", "--verbose", "--no-wait"]));
        var authentication = Obj(("type", S("api_key")), ("parameters", new JsonArray((JsonNode)Obj(("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")), ("description", S("Google Gemini API Key，通过 x-goog-api-key 传递，不落盘或回显。")), ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))));
        return Obj(("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)), ("name", S("Gemini 图片生成")), ("provider", S("gemini")), ("version", S(Version)), ("capabilities", Strings("image")), ("default_api_url", S(DefaultApiUrl)), ("request_schema_version", S(ContractVersion)), ("request_schema", schema), ("max_request_bytes", N(1_048_576)), ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray((JsonNode)task)), ("model_discovery", Obj(("source", S("api")), ("command", S("list-models")), ("path", S("models")), ("capability_information", S("provider")))), ("default_task", S("text-to-image")), ("default_models", Obj(("text-to-image", S(DefaultModel)))), ("authentication", authentication), ("extensions", new JsonObject()));
    }

    private static JsonArray Parameters() => new((JsonNode)Parameter("aspect_ratio", "--aspect-ratio", "string", "图片比例。", "auto", SupportedAspectRatios), (JsonNode)Parameter("num_images", "--num-images", "integer", "当前固定生成一张图片。", 1, null, 1, 1));

    private static JsonObject Parameter(string name, string cli, string type, string description, object defaultValue, string[]? values = null, int? minimum = null, int? maximum = null)
    {
        var parameter = Obj(("name", S(name)), ("cli_name", S(cli)), ("request_path", S("/parameters/" + name)), ("scope", S("common")), ("type", S(type)), ("required", B(false)), ("description", S(description)), ("sensitive", B(false)));
        if (defaultValue is string text) parameter["default"] = S(text); else parameter["default"] = N((int)defaultValue);
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
            parameters.Add(Obj(("name", S(field)), ("cli_name", S(option)), ("request_path", null), ("scope", S("common")), ("type", S(field is "timeout" or "num_images" ? "integer" : field is "try_run" or "verbose" or "no_wait" ? "boolean" : "string")), ("required", B(option == "--api-key" && auth || option == "--request-id")), ("description", S("协议参数。")), ("sensitive", B(option == "--api-key"))));
        }
        return Obj(("name", S(name)), ("description", S(name == "generate" ? "通过 Gemini 图片模型生成图片并保存产物。" : "读取 Gemini 技能协议数据。")), ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
