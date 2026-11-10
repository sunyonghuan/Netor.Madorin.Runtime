using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Zhipu.Video.Metadata;

internal static class ZhipuVideoMetadata
{
    public const string SkillId = "skill-zhipu-video";
    public const string DefaultModel = "cogvideox-3";
    public const string DefaultApiUrl = "https://open.bigmodel.cn/api";
    public static readonly string[] Sizes = ["1280x720", "720x1280", "1024x1024", "1920x1080", "1080x1920", "2048x1080", "3840x2160"];

    private static string Version => typeof(ZhipuVideoMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("zhipu")), ("capabilities", Strings("video")), ("runtime", S("native-aot")));

    public static JsonObject TaskContext(string taskId, string task, string model) => Obj(
        ("schema_version", S(ContractVersion)), ("skill_id", S(SkillId)), ("provider", S("zhipu")),
        ("task_id", S(taskId)), ("task", S(task)), ("model", S(model)), ("output_indices", new JsonArray(N(1))), ("provider_context", new JsonObject()));

    public static JsonObject BuildMetadata(string? id)
    {
        var name = typeof(ZhipuVideoMetadata).Assembly.GetManifestResourceNames().Single(item => item.EndsWith("request.schema.json", StringComparison.Ordinal));
        using var stream = typeof(ZhipuVideoMetadata).Assembly.GetManifestResourceStream(name)!;
        var schema = JsonNode.Parse(stream)!.AsObject();
        var generateOptions = new[] { "--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--duration", "--fps", "--size", "--width", "--height", "--reference-image", "--image-url", "--provider-zhipu-quality", "--provider-zhipu-with-audio", "--provider-zhipu-watermark", "--provider-zhipu-user-id", "--timeout", "--try-run", "--verbose", "--no-wait", "--output-format" };
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", generateOptions),
            Command("query", true, "TaskResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--timeout", "--wait", "--output-format", "--verbose"]),
            Command("download", true, "GenerateResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--output-dir", "--timeout", "--output-format", "--verbose"]));
        var tasks = new JsonArray(Task("text-to-video"), Task("image-to-video"));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("智谱 CogVideoX 视频生成")), ("provider", S("zhipu")), ("version", S(Version)),
            ("capabilities", Strings("video")), ("default_api_url", S(DefaultApiUrl)),
            ("request_schema_version", S(ContractVersion)), ("request_schema", schema), ("max_request_bytes", N(1_048_576)),
            ("commands", commands), ("models", new JsonArray()), ("tasks", tasks),
            ("model_discovery", Obj(("source", S("unsupported")), ("command", null), ("path", null), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-video")), ("default_models", Obj(("text-to-video", S(DefaultModel)), ("image-to-video", S(DefaultModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(
                ("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                ("description", S("智谱 API Key，通过 Authorization Bearer 发送。")), ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    private static JsonObject Task(string id) => Obj(
        ("id", S(id)), ("media_type", S("video")), ("parameters", Parameters()),
        ("parameter_conversions", new JsonArray(Obj(("rule_id", S("dimensions_to_size")), ("source_parameters", Strings("/parameters/width", "/parameters/height")), ("target_parameters", Strings("/parameters/size"))))),
        ("validation_schema", ZhipuVideoSchemas.Effective(id)), ("supports_partial", B(false)), ("supports_async", B(true)),
        ("task_context_required", B(true)), ("task_context_schema", ZhipuVideoSchemas.TaskContext()),
        ("attachment_limits", id == "image-to-video" ? new JsonArray(Obj(("role", S("reference")), ("max_count", N(1))), Obj(("role", S("source")), ("max_count", N(1)))) : new JsonArray()));

    private static JsonArray Parameters() => new(
        (JsonNode)Obj(("name", S("duration")), ("cli_name", S("--duration")), ("request_path", S("/parameters/duration")), ("scope", S("common")), ("type", S("integer")), ("required", B(false)), ("default", N(5)), ("enum", new JsonArray(N(5), N(10))), ("description", S("视频时长，秒。")), ("sensitive", B(false))),
        (JsonNode)Obj(("name", S("fps")), ("cli_name", S("--fps")), ("request_path", S("/parameters/fps")), ("scope", S("common")), ("type", S("integer")), ("required", B(false)), ("default", N(30)), ("enum", new JsonArray(N(30), N(60))), ("description", S("视频帧率。")), ("sensitive", B(false))),
        (JsonNode)Obj(("name", S("size")), ("cli_name", S("--size")), ("request_path", S("/parameters/size")), ("scope", S("common")), ("type", S("string")), ("required", B(false)), ("enum", Strings(Sizes)), ("description", S("CogVideoX-3 像素尺寸。")), ("sensitive", B(false))));

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            parameters.Add(Obj(("name", S(option[2..].Replace('-', '_'))), ("cli_name", S(option)), ("request_path", S("/" + option[2..].Replace('-', '_'))),
                ("scope", S("common")), ("type", S("string")), ("required", B(option is "--request-id" or "--api-key" && (option != "--request-id" || name != "generate") && auth || option == "--request-id")),
                ("description", S("智谱视频协议参数。")), ("sensitive", B(option == "--api-key"))));
        }
        return Obj(("name", S(name)), ("description", S("智谱视频命令。")), ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
