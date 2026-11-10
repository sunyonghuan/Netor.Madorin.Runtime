using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Kling.Video.Metadata;

internal static class KlingVideoMetadata
{
    public const string SkillId = "skill-kling-video";
    public const string DefaultModel = "kling-4.0";
    public const string DefaultApiUrl = "https://api-beijing.klingai.com";
    public const string DefaultResolution = "1080p";
    public const string DefaultAspectRatio = "16:9";
    public static readonly string[] Resolutions = ["720p", "1080p"];
    public static readonly string[] AspectRatios = ["21:9", "16:9", "9:16", "1:1"];

    private static string Version => typeof(KlingVideoMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("kling")), ("capabilities", Strings("video")), ("runtime", S("native-aot")));

    public static JsonObject TaskContext(string taskId, string task, string model) => Obj(
        ("schema_version", S(ContractVersion)), ("skill_id", S(SkillId)), ("provider", S("kling")),
        ("task_id", S(taskId)), ("task", S(task)), ("model", S(model)), ("output_indices", new JsonArray(N(1))), ("provider_context", new JsonObject()));

    public static JsonObject BuildMetadata(string? id)
    {
        var name = typeof(KlingVideoMetadata).Assembly.GetManifestResourceNames().Single(item => item.EndsWith("request.schema.json", StringComparison.Ordinal));
        using var stream = typeof(KlingVideoMetadata).Assembly.GetManifestResourceStream(name)!;
        var schema = JsonNode.Parse(stream)!.AsObject();
        var generate = new[] { "--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt", "--duration", "--resolution", "--reference-image", "--image-url", "--provider-kling-aspect-ratio", "--provider-kling-watermark", "--timeout", "--try-run", "--verbose", "--no-wait", "--output-format" };
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", generate),
            Command("query", true, "TaskResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--timeout", "--wait", "--output-format", "--verbose"]),
            Command("download", true, "GenerateResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--output-dir", "--timeout", "--output-format", "--verbose"]));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("可灵视频生成")), ("provider", S("kling")), ("version", S(Version)),
            ("capabilities", Strings("video")), ("default_api_url", S(DefaultApiUrl)),
            ("request_schema_version", S(ContractVersion)), ("request_schema", schema), ("max_request_bytes", N(1_048_576)),
            ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray(Task("text-to-video"), Task("image-to-video"))),
            ("model_discovery", Obj(("source", S("unsupported")), ("command", null), ("path", null), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-video")), ("default_models", Obj(("text-to-video", S(DefaultModel)), ("image-to-video", S(DefaultModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(
                ("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                ("description", S("可灵 API Key，通过 Authorization Bearer 发送。")), ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    private static JsonObject Task(string id) => Obj(
        ("id", S(id)), ("media_type", S("video")), ("parameters", Parameters()),
        ("parameter_conversions", new JsonArray()), ("validation_schema", KlingVideoSchemas.Effective(id)),
        ("supports_partial", B(false)), ("supports_async", B(true)), ("task_context_required", B(true)),
        ("task_context_schema", KlingVideoSchemas.TaskContext()),
        ("attachment_limits", id == "image-to-video" ? new JsonArray(Obj(("role", S("reference")), ("max_count", N(1))), Obj(("role", S("source")), ("max_count", N(1)))) : new JsonArray()));

    private static JsonArray Parameters() => new(
        (JsonNode)Obj(("name", S("duration")), ("cli_name", S("--duration")), ("request_path", S("/parameters/duration")), ("scope", S("common")), ("type", S("integer")), ("required", B(false)), ("minimum", N(3)), ("maximum", N(30)), ("description", S("显式视频时长，秒。缺省不发送。")), ("sensitive", B(false))),
        (JsonNode)Obj(("name", S("resolution")), ("cli_name", S("--resolution")), ("request_path", S("/parameters/resolution")), ("scope", S("common")), ("type", S("string")), ("required", B(false)), ("default", S(DefaultResolution)), ("enum", Strings(Resolutions)), ("description", S("Kling 4.0 清晰度。")), ("sensitive", B(false))));

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            parameters.Add(Obj(("name", S(option[2..].Replace('-', '_'))), ("cli_name", S(option)), ("request_path", S("/" + option[2..].Replace('-', '_'))),
                ("scope", S("common")), ("type", S("string")), ("required", B(option == "--request-id" || option == "--api-key" && auth)),
                ("description", S("可灵视频协议参数。")), ("sensitive", B(option == "--api-key"))));
        }

        return Obj(("name", S(name)), ("description", S("可灵视频命令。")), ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
