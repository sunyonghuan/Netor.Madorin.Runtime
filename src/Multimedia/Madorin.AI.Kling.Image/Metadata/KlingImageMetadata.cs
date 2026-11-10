using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Kling.Image.Metadata;

internal static class KlingImageMetadata
{
    public const string SkillId = "skill-kling-image";
    public const string DefaultModel = "kling-v3";
    public const string DefaultApiUrl = "https://api-beijing.klingai.com";
    public const string DefaultResolution = "1k";
    public const string DefaultAspectRatio = "16:9";
    public static readonly string[] Resolutions = ["1k", "2k"];
    public static readonly string[] AspectRatios = ["16:9", "9:16", "1:1", "4:3", "3:4", "3:2", "2:3", "21:9"];

    private static string Version => typeof(KlingImageMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("kling")), ("capabilities", Strings("image")), ("runtime", S("native-aot")));

    public static JsonObject TaskContext(string taskId, string model, int count)
    {
        var indices = new JsonArray();
        for (var index = 1; index <= count; index++) indices.Add(N(index));
        return Obj(
            ("schema_version", S(ContractVersion)), ("skill_id", S(SkillId)), ("provider", S("kling")),
            ("task_id", S(taskId)), ("task", S("text-to-image")), ("model", S(model)),
            ("output_indices", indices), ("provider_context", new JsonObject()));
    }

    public static JsonObject BuildMetadata(string? id)
    {
        var name = typeof(KlingImageMetadata).Assembly.GetManifestResourceNames().Single(item => item.EndsWith("request.schema.json", StringComparison.Ordinal));
        using var stream = typeof(KlingImageMetadata).Assembly.GetManifestResourceStream(name)!;
        var schema = JsonNode.Parse(stream)!.AsObject();
        var generate = new[] { "--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--provider-kling-negative-prompt", "--provider-kling-resolution", "--provider-kling-count", "--provider-kling-aspect-ratio", "--provider-kling-watermark", "--timeout", "--try-run", "--verbose", "--no-wait", "--output-format" };
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", generate),
            Command("query", true, "TaskResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--timeout", "--wait", "--output-format", "--verbose"]),
            Command("download", true, "GenerateResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--output-dir", "--timeout", "--output-format", "--verbose"]));
        var task = Obj(
            ("id", S("text-to-image")), ("media_type", S("image")), ("parameters", new JsonArray()),
            ("parameter_conversions", new JsonArray()), ("validation_schema", KlingImageSchemas.Effective()),
            ("supports_partial", B(false)), ("supports_async", B(true)), ("task_context_required", B(true)),
            ("task_context_schema", KlingImageSchemas.TaskContext()), ("attachment_limits", new JsonArray()));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("可灵图片生成")), ("provider", S("kling")), ("version", S(Version)),
            ("capabilities", Strings("image")), ("default_api_url", S(DefaultApiUrl)),
            ("request_schema_version", S(ContractVersion)), ("request_schema", schema), ("max_request_bytes", N(1_048_576)),
            ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray((JsonNode)task)),
            ("model_discovery", Obj(("source", S("unsupported")), ("command", null), ("path", null), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-image")), ("default_models", Obj(("text-to-image", S(DefaultModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(
                ("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                ("description", S("可灵 API Key，通过 Authorization Bearer 发送，不写入请求文件或任务上下文。")),
                ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            parameters.Add(Obj(("name", S(option[2..].Replace('-', '_'))), ("cli_name", S(option)), ("request_path", S("/" + option[2..].Replace('-', '_'))),
                ("scope", S("common")), ("type", S("string")), ("required", B(option == "--request-id" || option == "--api-key" && auth)),
                ("description", S("可灵图片协议参数。")), ("sensitive", B(option == "--api-key"))));
        }

        return Obj(("name", S(name)), ("description", S("可灵图片命令。")), ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
