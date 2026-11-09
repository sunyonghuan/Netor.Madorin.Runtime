using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Aliyun.Image.Metadata;

internal static class AliyunImageSchemas
{
    public static JsonObject Effective()
    {
        var parameterProperties = Obj(("size", Enum(AliyunImageMetadata.SupportedSizes)),
            ("num_images", Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(4)))),
            ("negative_prompt", Obj(("type", S("string")))), ("prompt_extend", Obj(("type", S("boolean")))),
            ("watermark", Obj(("type", S("boolean")))),
            ("seed", Obj(("type", S("integer")), ("minimum", N(0)), ("maximum", N(int.MaxValue)))));
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("size", "num_images")), ("properties", parameterProperties));
        var execution = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")),
            ("properties", Obj(("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))), ("try_run", Obj(("type", S("boolean")))),
                ("verbose", Obj(("type", S("boolean")))), ("no_wait", Obj(("const", B(false)))), ("output_format", Constant("json")))));
        var properties = Obj(("schema_version", Constant("1.0")), ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
            ("task", Constant("text-to-image")), ("model", Obj(("type", S("string")), ("minLength", N(1)))), ("prompt", Obj(("type", S("string")), ("minLength", N(1)))),
            ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))), ("parameters", parameters),
            ("provider_parameters", ProviderParameters(parameterProperties)), ("execution", execution));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", properties));
    }

    private static JsonObject ProviderParameters(JsonObject parameterProperties) => Obj(
        ("type", S("object")),
        ("properties", Obj(("aliyun", Obj(("type", S("object")), ("additionalProperties", B(false)), ("properties", parameterProperties))))),
        ("additionalProperties", B(false)));

    private static JsonObject Constant(string value) => Obj(("type", S("string")), ("const", S(value)));
    private static JsonObject Enum(params string[] values) => Obj(("type", S("string")), ("enum", Strings(values)));
}







