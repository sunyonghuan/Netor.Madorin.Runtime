using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Video.Metadata;

internal static class ZhipuVideoSchemas
{
    public static JsonObject Effective(string task)
    {
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("duration", Obj(("type", S("integer")), ("enum", new JsonArray(N(5), N(10))))),
            ("fps", Obj(("type", S("integer")), ("enum", new JsonArray(N(30), N(60))))),
            ("size", Obj(("type", S("string")), ("enum", Strings(ZhipuVideoMetadata.Sizes)))),
            ("width", Obj(("type", S("integer")), ("minimum", N(1)))),
            ("height", Obj(("type", S("integer")), ("minimum", N(1)))))));
        var zhipu = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("quality", Obj(("type", S("string")), ("enum", Strings("speed", "quality")))),
            ("with_audio", Obj(("type", S("boolean")))),
            ("watermark_enabled", Obj(("type", S("boolean")))),
            ("user_id", Obj(("type", S("string")), ("minLength", N(6)), ("maxLength", N(128)))))));
        var required = task == "text-to-video"
            ? Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")
            : Strings("schema_version", "request_id", "task", "model", "parameters", "execution", "attachments", "provider_parameters");
        return Obj(("type", S("object")), ("additionalProperties", B(false)), ("required", required), ("properties", Obj(
            ("schema_version", Obj(("const", S("1.0")))),
            ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
            ("task", Obj(("const", S(task)))),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("prompt", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(512)))),
            ("attachments", Obj(("type", S("array")), ("maxItems", N(task == "image-to-video" ? 1 : 0)))),
            ("parameters", parameters),
            ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("zhipu", zhipu))))),
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
            ("skill_id", Obj(("const", S(ZhipuVideoMetadata.SkillId)))),
            ("provider", Obj(("const", S("zhipu")))),
            ("task_id", Obj(("type", S("string")), ("minLength", N(1)))),
            ("task", Obj(("type", S("string")), ("enum", Strings("text-to-video", "image-to-video")))),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("output_indices", Obj(("type", S("array")), ("minItems", N(1)), ("maxItems", N(1)))),
            ("provider_context", Obj(("type", S("object")), ("maxProperties", N(0)))))));
}
