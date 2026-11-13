using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Aliyun.Video.Metadata;

internal static class AliyunVideoSchemas
{
    public static JsonObject Effective(string task)
    {
        var properties = Obj(
            ("duration", Obj(("type", S("integer")), ("minimum", N(2)), ("maximum", N(15)))),
            ("resolution", Obj(("type", S("string")), ("enum", Strings(AliyunVideoMetadata.Resolutions)))),
            ("negative_prompt", Obj(("type", S("string")), ("maxLength", N(500)))));
        if (task == "text-to-video") properties["aspect_ratio"] = Obj(("type", S("string")), ("enum", Strings(AliyunVideoMetadata.Ratios)));
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", properties));
        var aliyun = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("prompt_extend", Obj(("type", S("boolean")))),
            ("watermark", Obj(("type", S("boolean")))),
            ("seed", Obj(("type", S("integer")), ("minimum", N(0)), ("maximum", N(int.MaxValue)))),
            ("audio_url", Obj(("type", S("string")), ("minLength", N(1)))))));
        var required = task == "text-to-video"
            ? Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")
            : Strings("schema_version", "request_id", "task", "model", "parameters", "execution", "attachments", "provider_parameters");
        return Obj(("type", S("object")), ("additionalProperties", B(false)), ("required", required), ("properties", Obj(
            ("schema_version", Obj(("const", S("1.0")))),
            ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
            ("task", Obj(("const", S(task)))),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("prompt", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(5000)))),
            ("attachments", Obj(("type", S("array")), ("maxItems", N(task == "image-to-video" ? 1 : 0)))),
            ("parameters", parameters),
            ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("aliyun", aliyun))))),
            ("execution", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
                ("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))),
                ("try_run", Obj(("type", S("boolean")))),
                ("verbose", Obj(("type", S("boolean")))),
                ("no_wait", Obj(("type", S("boolean")))),
                ("output_format", Obj(("const", S("json"))))))))));
    }

    public static JsonObject TaskContext() => Obj(("type", S("object")), ("additionalProperties", B(false)),
        ("required", Strings("schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context")),
        ("properties", Obj(
            ("schema_version", Obj(("const", S("1.0")))),
            ("skill_id", Obj(("const", S(AliyunVideoMetadata.SkillId)))),
            ("provider", Obj(("const", S("aliyun")))),
            ("task_id", Obj(("type", S("string")), ("minLength", N(1)))),
            ("task", Obj(("type", S("string")), ("enum", Strings("text-to-video", "image-to-video")))),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("output_indices", Obj(("type", S("array")), ("minItems", N(1)), ("maxItems", N(1)))),
            ("provider_context", Obj(("type", S("object")), ("maxProperties", N(0)))))));
}
