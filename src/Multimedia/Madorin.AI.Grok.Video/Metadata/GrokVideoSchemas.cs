using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Video.Metadata;

internal static class GrokVideoSchemas
{
    public static JsonObject Effective()
    {
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("duration", "aspect_ratio", "resolution")),
            ("properties", Obj(
                ("duration", Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(15)))),
                ("aspect_ratio", Enum(GrokVideoMetadata.SupportedAspectRatios)),
                ("resolution", Enum(GrokVideoMetadata.SupportedResolutions)))));
        var execution = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")),
            ("properties", Obj(
                ("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))),
                ("try_run", Obj(("type", S("boolean")))),
                ("verbose", Obj(("type", S("boolean")))),
                ("no_wait", Obj(("type", S("boolean")))),
                ("output_format", Constant("json")))));
        var properties = Obj(
            ("schema_version", Constant("1.0")),
            ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
            ("task", Constant("text-to-video")),
            ("model", Obj(("type", S("string")), ("minLength", N(1)))),
            ("prompt", Obj(("type", S("string")), ("minLength", N(1)))),
            ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))),
            ("parameters", parameters),
            ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)))),
            ("execution", execution));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", properties));
    }

    public static JsonObject TaskContext()
    {
        var providerContext = Obj(("type", S("object")), ("additionalProperties", B(false)));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context")),
            ("properties", Obj(
                ("schema_version", Constant("1.0")),
                ("skill_id", Constant(GrokVideoMetadata.SkillId)),
                ("provider", Constant("grok")),
                ("task_id", Obj(("type", S("string")), ("minLength", N(1)))),
                ("task", Constant("text-to-video")),
                ("model", Obj(("type", S("string")), ("minLength", N(1)))),
                ("output_indices", Obj(("type", S("array")), ("minItems", N(1)), ("maxItems", N(1)),
                    ("items", Obj(("type", S("integer")), ("const", N(1)))))),
                ("provider_context", providerContext))));
    }

    private static JsonObject Constant(string value) => Obj(("type", S("string")), ("const", S(value)));

    private static JsonObject Enum(params string[] values) => Obj(("type", S("string")), ("enum", Strings(values)));
}
