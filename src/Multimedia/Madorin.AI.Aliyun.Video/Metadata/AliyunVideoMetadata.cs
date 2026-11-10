using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.Protocol.SkillProtocol;

namespace Madorin.AI.Aliyun.Video.Metadata;

internal static class AliyunVideoMetadata
{
    public const string SkillId = "skill-aliyun-video";
    public const string DefaultTextModel = "wan2.7-t2v";
    public const string DefaultImageModel = "wan2.7-i2v-2026-04-25";
    public const string DefaultApiUrl = "https://dashscope.aliyuncs.com/api/v1";
    public static readonly string[] Resolutions = ["720P", "1080P"];
    public static readonly string[] Ratios = ["16:9", "9:16", "1:1", "4:3", "3:4"];

    private static string Version => typeof(AliyunVideoMetadata).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static JsonObject BuildVersion(string? id) => Obj(
        ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
        ("version", S(Version)), ("provider", S("aliyun")), ("capabilities", Strings("video")), ("runtime", S("native-aot")));

    public static JsonObject TaskContext(string taskId, string task, string model) => Obj(
        ("schema_version", S(ContractVersion)), ("skill_id", S(SkillId)), ("provider", S("aliyun")),
        ("task_id", S(taskId)), ("task", S(task)), ("model", S(model)), ("output_indices", new JsonArray(N(1))), ("provider_context", new JsonObject()));

    public static JsonObject BuildMetadata(string? id)
    {
        using var stream = typeof(AliyunVideoMetadata).Assembly.GetManifestResourceStream("Madorin.AI.Aliyun.Video.request.schema.json") ?? throw new InvalidOperationException("请求 Schema 资源缺失。");
        var schema = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("请求 Schema 无效。");
        var generateOptions = new[]
        {
            "--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt",
            "--duration", "--resolution", "--aspect-ratio", "--negative-prompt", "--reference-image", "--image-url", "--first-frame-url",
            "--provider-aliyun-prompt-extend", "--provider-aliyun-watermark", "--provider-aliyun-seed", "--provider-aliyun-audio-url",
            "--timeout", "--try-run", "--verbose", "--no-wait", "--output-format"
        };
        var commands = new JsonArray(
            Command("metadata", false, "SkillMetadata", ["--request-id", "--output-format", "--verbose"]),
            Command("list-models", true, "ModelList", ["--request-id", "--api-url", "--api-key", "--timeout", "--output-format", "--verbose"]),
            Command("version", false, "VersionInfo", ["--request-id", "--output-format", "--verbose"]),
            Command("generate", true, "GenerateResponse", generateOptions),
            Command("query", true, "TaskResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--timeout", "--wait", "--output-format", "--verbose"]),
            Command("download", true, "GenerateResponse", ["--request-id", "--task-id", "--task-context-file", "--api-url", "--api-key", "--output-dir", "--timeout", "--output-format", "--verbose"]));
        return Obj(
            ("schema_version", S(ContractVersion)), ("response_id", S(id)), ("skill_id", S(SkillId)),
            ("name", S("阿里云百炼万相视频")), ("provider", S("aliyun")), ("version", S(Version)),
            ("capabilities", Strings("video")), ("default_api_url", S(DefaultApiUrl)),
            ("request_schema_version", S(ContractVersion)), ("request_schema", schema), ("max_request_bytes", N(1_048_576)),
            ("commands", commands), ("models", new JsonArray()), ("tasks", new JsonArray((JsonNode)Task("text-to-video"), (JsonNode)Task("image-to-video"))),
            ("model_discovery", Obj(("source", S("unsupported")), ("command", null), ("path", null), ("capability_information", S("unknown")))),
            ("default_task", S("text-to-video")), ("default_models", Obj(("text-to-video", S(DefaultTextModel)), ("image-to-video", S(DefaultImageModel)))),
            ("authentication", Obj(("type", S("api_key")), ("parameters", new JsonArray(Obj(
                ("name", S("api_key")), ("cli_name", S("--api-key")), ("request_path", null), ("scope", S("common")),
                ("description", S("阿里云百炼 API Key，通过 Authorization Bearer 发送，不落盘或回显。")), ("type", S("string")), ("required", B(true)), ("sensitive", B(true))))))),
            ("extensions", new JsonObject()));
    }

    private static JsonObject Task(string id) => Obj(
        ("id", S(id)), ("media_type", S("video")), ("parameters", Parameters(id)),
        ("parameter_conversions", id == "text-to-video"
            ? new JsonArray(
                Obj(("rule_id", S("aspect_ratio_to_ratio")), ("source_parameters", Strings("/parameters/aspect_ratio")), ("target_parameters", Strings("/parameters/ratio"))),
                Obj(("rule_id", S("resolution_case")), ("source_parameters", Strings("/parameters/resolution")), ("target_parameters", Strings("/parameters/resolution"))))
            : new JsonArray(Obj(("rule_id", S("resolution_case")), ("source_parameters", Strings("/parameters/resolution")), ("target_parameters", Strings("/parameters/resolution"))))),
        ("validation_schema", AliyunVideoSchemas.Effective(id)), ("supports_partial", B(false)), ("supports_async", B(true)),
        ("task_context_required", B(true)), ("task_context_schema", AliyunVideoSchemas.TaskContext()),
        ("attachment_limits", id == "image-to-video"
            ? new JsonArray(Obj(("role", S("first_frame")), ("max_count", N(1))), Obj(("role", S("reference")), ("max_count", N(1))), Obj(("role", S("source")), ("max_count", N(1))))
            : new JsonArray()));

    private static JsonArray Parameters(string task)
    {
        var parameters = new JsonArray(
            (JsonNode)Obj(("name", S("duration")), ("cli_name", S("--duration")), ("request_path", S("/parameters/duration")), ("scope", S("common")), ("type", S("integer")), ("required", B(false)), ("default", N(5)), ("minimum", N(2)), ("maximum", N(15)), ("description", S("视频时长，秒。")), ("sensitive", B(false))),
            (JsonNode)Obj(("name", S("resolution")), ("cli_name", S("--resolution")), ("request_path", S("/parameters/resolution")), ("scope", S("common")), ("type", S("string")), ("required", B(false)), ("default", S("1080P")), ("enum", Strings(Resolutions)), ("description", S("万相 2.7 分辨率档位。")), ("sensitive", B(false))),
            (JsonNode)Obj(("name", S("negative_prompt")), ("cli_name", S("--negative-prompt")), ("request_path", S("/parameters/negative_prompt")), ("scope", S("common")), ("type", S("string")), ("required", B(false)), ("description", S("反向提示词，不超过 500 个字符。")), ("sensitive", B(false))));
        if (task == "text-to-video")
        {
            parameters.Add(Obj(("name", S("aspect_ratio")), ("cli_name", S("--aspect-ratio")), ("request_path", S("/parameters/aspect_ratio")), ("scope", S("common")), ("type", S("string")), ("required", B(false)), ("default", S("16:9")), ("enum", Strings(Ratios)), ("description", S("文生视频宽高比，提交时映射为官方 ratio。")), ("sensitive", B(false))));
        }

        return parameters;
    }

    private static JsonObject Command(string name, bool auth, string response, string[] options)
    {
        var parameters = new JsonArray();
        foreach (var option in options)
        {
            var required = option == "--request-id" || auth && option == "--api-key" ||
                name is "query" or "download" && option is "--task-id" or "--task-context-file" ||
                name == "download" && option == "--output-dir";
            parameters.Add(Obj(("name", S(option[2..].Replace('-', '_'))), ("cli_name", S(option)), ("request_path", S("/" + option[2..].Replace('-', '_'))),
                ("scope", S(option.StartsWith("--provider-aliyun-", StringComparison.Ordinal) ? "provider" : "common")),
                ("type", S("string")), ("required", B(required)),
                ("description", S("阿里云万相视频协议参数。")), ("sensitive", B(option == "--api-key"))));
        }

        return Obj(("name", S(name)), ("description", S(name == "generate" ? "通过万相 2.7 提交视频任务，并在等待时下载 MP4。" : "读取阿里云万相视频协议数据。")),
            ("parameters", parameters), ("authentication_required", B(auth)), ("response_type", S(response)));
    }
}
