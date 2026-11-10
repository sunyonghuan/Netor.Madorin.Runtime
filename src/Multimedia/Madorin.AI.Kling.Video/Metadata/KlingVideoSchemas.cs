using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Video.Metadata;

internal static class KlingVideoSchemas
{
    public static JsonObject Effective(string task)
    {
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("duration", Obj(("type", S("integer")), ("minimum", N(3)), ("maximum", N(30)))),
            ("resolution", Obj(("type", S("string")), ("enum", Strings(KlingVideoMetadata.Resolutions)))))));
        var kling = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("aspect_ratio", Obj(("type", S("string")), ("enum", Strings(KlingVideoMetadata.AspectRatios)))),
            ("watermark_enabled", Obj(("type", S("boolean")))))));
        var execution = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")),
            ("properties", Obj(
                ("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))),
                ("try_run", Obj(("type", S("boolean")))),
                ("verbose", Obj(("type", S("boolean")))),
                ("no_wait", Obj(("type", S("boolean")))),
                ("output_format", Obj(("const", S("json")))))));
        var prompt = task == "text-to-video"
            ? Obj(("type", S("string")), ("minLength", N(1)))
            : Obj(("type", S("string")));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", Obj(
                ("schema_version", Obj(("const", S("1.0")))),
                ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
                ("task", Obj(("const", S(task)))),
                ("model", Obj(("type", S("string")), ("minLength", N(1)))),
                ("prompt", prompt),
                ("attachments", Obj(("type", S("array")), ("maxItems", N(task == "image-to-video" ? 1 : 0)))),
                ("parameters", parameters),
                ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("kling", kling))))),
                ("execution", execution))));
    }

    public static JsonObject TaskContext() => Obj(("type", S("object")), ("additionalProperties", B(false)),
        ("required", Strings("schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context")),
        ("properties", Obj(
            ("schema_version", Obj(("const", S("1.0")))),
            ("skill_id", Obj(("const", S(KlingVideoMetadata.SkillId)))),
            ("provider", Obj(("const", S("kling")))),
            ("task_id", Obj(("type", S("string")), ("minLength", N(1)))),
            ("task", Obj(("type", S("string")), ("enum", Strings("text-to-video", "image-to-video")))),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("output_indices", Obj(("type", S("array")), ("minItems", N(1)), ("maxItems", N(1)))),
            ("provider_context", Obj(("type", S("object")), ("additionalProperties", B(false)), ("maxProperties", N(0)))))));
}
