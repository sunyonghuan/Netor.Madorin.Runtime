using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Bfl.Image.Metadata;

internal static class BflImageSchemas
{
    public static JsonObject Effective()
    {
        var parameterProperties = Obj(
            ("width", Obj(("type", S("integer")), ("minimum", N(256)), ("maximum", N(6256)))),
            ("height", Obj(("type", S("integer")), ("minimum", N(256)), ("maximum", N(6256)))),
            ("seed", Obj(("type", S("integer")), ("minimum", N(0)))),
            ("safety_tolerance", Obj(("type", S("integer")), ("minimum", N(0)), ("maximum", N(5)))),
            ("output_format", Obj(("type", S("string")), ("enum", Strings("jpeg", "png", "webp")))),
            ("disable_pup", Obj(("type", S("boolean")))));

        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", parameterProperties));
        var executionProperties = Obj(
            ("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
            ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))),
            ("try_run", Obj(("type", S("boolean")))),
            ("verbose", Obj(("type", S("boolean")))),
            ("no_wait", Obj(("const", B(false)))),
            ("output_format", Obj(("const", S("json")))));
        var execution = Obj(("type", S("object")), ("additionalProperties", B(false)), ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")), ("properties", executionProperties));
        var providerBfl = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("disable_pup", Obj(("type", S("boolean")))))));
        var provider = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("bfl", providerBfl))));

        return Obj(
            ("type", S("object")),
            ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", Obj(
                ("schema_version", Obj(("const", S("1.0")))),
                ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
                ("task", Obj(("const", S("text-to-image")))),
                ("model", Obj(("type", S("string")), ("minLength", N(1)))),
                ("prompt", Obj(("type", S("string")), ("minLength", N(1)))),
                ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))),
                ("parameters", parameters),
                ("provider_parameters", provider),
                ("execution", execution))));
    }
}
