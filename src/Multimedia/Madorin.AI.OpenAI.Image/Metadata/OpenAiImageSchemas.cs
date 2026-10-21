using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Image.Metadata;

internal static class OpenAiImageSchemas
{
    public static JsonObject Effective()
    {
        var parameters = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("size", "quality", "num_images")),
            ("properties", Obj(("size", Enum(OpenAiImageMetadata.SupportedSizes)), ("quality", Enum(OpenAiImageMetadata.SupportedQualities)),
                ("num_images", Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(10)))))));
        return Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "execution", "attachments", "provider_parameters")),
            ("properties", Obj(("schema_version", Constant("1.0")), ("request_id", Obj(("type", S("string")), ("minLength", N(1)), ("maxLength", N(128)))),
                ("task", Constant("text-to-image")), ("model", Obj(("type", S("string")), ("minLength", N(1)))), ("prompt", Obj(("type", S("string")), ("minLength", N(1)))),
                ("attachments", Obj(("type", S("array")), ("maxItems", N(0)))), ("parameters", parameters),
                ("provider_parameters", Obj(("type", S("object")), ("additionalProperties", B(false)), ("required", Strings("openai")),
                    ("properties", Obj(("openai", Obj(("type", S("object")), ("additionalProperties", B(false)), ("required", Strings("output_format")),
                        ("properties", Obj(("output_format", Enum("png", "jpeg", "webp")))))))))),
                ("execution", Obj(("type", S("object")), ("additionalProperties", B(false)),
                    ("required", Strings("timeout", "try_run", "verbose", "no_wait", "output_format")),
                    ("properties", Obj(("output_dir", Obj(("type", S("string")), ("minLength", N(1)))),
                        ("timeout", Obj(("type", S("integer")), ("minimum", N(1)))), ("try_run", Obj(("type", S("boolean")))),
                        ("verbose", Obj(("type", S("boolean")))), ("no_wait", Obj(("const", B(false)))), ("output_format", Constant("json")))))))));
    }

    public static JsonObject DimensionsConversion() => Obj(("id", S("dimensions_to_size")),
        ("source_parameters", Strings("/parameters/width", "/parameters/height")), ("target_parameters", Strings("/parameters/size")),
        ("description", S("宽高像素成对提供，精确转换为 widthxheight；仅接受模型支持的尺寸。显式 size 必须一致，不裁剪、不取整。")),
        ("source_schema", Obj(("type", S("object")), ("required", Strings("parameters")), ("properties", Obj(("parameters", Obj(("type", S("object")),
            ("required", Strings("width", "height")), ("properties", Obj(("width", PositiveInteger()), ("height", PositiveInteger()))))))))));

    private static JsonObject PositiveInteger() => Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(int.MaxValue)));
    public static JsonObject FormatConversion() => Obj(("id", S("image_format_to_openai")),
        ("source_parameters", Strings("/parameters/output_format")), ("target_parameters", Strings("/provider_parameters/openai/output_format")),
        ("description", S("兼容旧图片请求的 output_format 字符串，原值等价映射到 OpenAI 编码格式；与显式目标冲突则失败。")),
        ("source_schema", Obj(("type", S("object")), ("required", Strings("parameters")), ("properties", Obj(("parameters", Obj(("type", S("object")),
            ("required", Strings("output_format")), ("properties", Obj(("output_format", Enum("png", "jpeg", "webp")))))))))));
    private static JsonObject Constant(string value) => Obj(("type", S("string")), ("const", S(value)));
    internal static JsonObject Enum(params string[] values) => Obj(("type", S("string")), ("enum", Strings(values)));
}
