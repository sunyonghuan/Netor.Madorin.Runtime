using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Image.Metadata;

internal static class KlingImageSchemas
{
    public static JsonObject Effective()
    {
        var kling = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("negative_prompt", Obj(("type", S("string")), ("maxLength", N(2500)))),
            ("resolution", Obj(("type", S("string")), ("enum", Strings(KlingImageMetadata.Resolutions)))),
            ("count", Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(9)))),
            ("aspect_ratio", Obj(("type", S("string")), ("enum", Strings(KlingImageMetadata.AspectRatios)))),
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
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", Obj(
                ("schema_version", Obj(("const", S("1.0")))),
                ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
                ("task", Obj(("const", S("text-to-image")))),
                ("model", Obj(("type", S("string")), ("minLength", N(1)))),
                ("prompt", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(2500)))),
                ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))),
                ("parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", new JsonObject()))),
                ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("kling", kling))))),
                ("execution", execution))));
    }

    public static JsonObject TaskContext() => Obj(("type", S("object")), ("additionalProperties", B(false)),
        ("required", Strings("schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context")),
        ("properties", Obj(
            ("schema_version", Obj(("const", S("1.0")))),
            ("skill_id", Obj(("const", S(KlingImageMetadata.SkillId)))),
            ("provider", Obj(("const", S("kling")))),
            ("task_id", Obj(("type", S("string")), ("minLength", N(1)))),
            ("task", Obj(("const", S("text-to-image")))),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("output_indices", Obj(("type", S("array")), ("minItems", N(1)), ("maxItems", N(9)))),
            ("provider_context", Obj(("type", S("object")), ("additionalProperties", B(false)), ("maxProperties", N(0)))))));
}
