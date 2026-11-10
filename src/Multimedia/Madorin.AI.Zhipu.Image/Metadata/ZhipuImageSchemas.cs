using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Image.Metadata;

internal static class ZhipuImageSchemas
{
    public static JsonObject Effective()
    {
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("size", Obj(("type", S("string")), ("minLength", N(3)))),
            ("width", Obj(("type", S("integer")), ("minimum", N(512)), ("maximum", N(2048)))),
            ("height", Obj(("type", S("integer")), ("minimum", N(512)), ("maximum", N(2048)))),
            ("quality", Obj(("type", S("string")), ("enum", Strings("standard", "hd")))))));
        var execution = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")),
            ("properties", Obj(
                ("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))),
                ("try_run", Obj(("type", S("boolean")))),
                ("verbose", Obj(("type", S("boolean")))),
                ("no_wait", Obj(("const", B(false)))),
                ("output_format", Obj(("const", S("json")))))));
        var zhipu = Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(
            ("watermark_enabled", Obj(("type", S("boolean")))),
            ("user_id", Obj(("type", S("string")), ("minLength", N(6)), ("maxLength", N(128)))))));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", Obj(
                ("schema_version", Obj(("const", S("1.0")))),
                ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
                ("task", Obj(("const", S("text-to-image")))),
                ("model", Obj(("type", S("string")), ("minLength", N(1)))),
                ("prompt", Obj(("type", S("string")), ("minLength", N(1)))),
                ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))),
                ("parameters", parameters),
                ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", Obj(("zhipu", zhipu))))),
                ("execution", execution))));
    }
}
