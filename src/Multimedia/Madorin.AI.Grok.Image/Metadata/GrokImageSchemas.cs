using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Image.Metadata;

internal static class GrokImageSchemas
{
    public static JsonObject Effective()
    {
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("aspect_ratio", "num_images")),
            ("properties", Obj(("aspect_ratio", Enum(GrokImageMetadata.SupportedAspectRatios)),
                ("num_images", Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(10)))))));
        var execution = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")),
            ("properties", Obj(("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))), ("try_run", Obj(("type", S("boolean")))),
                ("verbose", Obj(("type", S("boolean")))), ("no_wait", Obj(("const", B(false)))), ("output_format", Constant("json")))));
        var properties = Obj(("schema_version", Constant("1.0")), ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
            ("task", Constant("text-to-image")), ("model", Obj(("type", S("string")), ("minLength", N(1)))), ("prompt", Obj(("type", S("string")), ("minLength", N(1)))),
            ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))), ("parameters", parameters),
            ("provider_parameters", Obj(("type", S("object")), ("maxProperties", N(0)))), ("execution", execution));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", properties));
    }

    private static JsonObject Constant(string value) => Obj(("type", S("string")), ("const", S(value)));
    private static JsonObject Enum(params string[] values) => Obj(("type", S("string")), ("enum", Strings(values)));
}
