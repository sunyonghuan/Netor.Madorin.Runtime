using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Video.Metadata;

internal static class VideoSchemas
{
    public static JsonObject Text() => Obj(("type", S("string")), ("minLength", N(1)));
    public static JsonObject Integer() => Obj(("type", S("integer")), ("minimum", N(1)), ("maximum", N(int.MaxValue)));
    public static JsonObject Enum(params string[] values) => Obj(("type", S("string")), ("enum", Strings(values)));
    public static JsonObject Constant(string value) => Obj(("type", S("string")), ("const", S(value)));

    public static JsonObject Context() => Obj(("type", S("object")), ("additionalProperties", B(false)),
        ("required", Strings("schema_version", "skill_id", "provider", "task_id", "task", "model", "output_indices", "provider_context")),
        ("properties", Obj(("schema_version", Constant("1.0")), ("skill_id", Constant("skill-openai-video")),
            ("provider", Constant("openai")), ("task_id", Text()), ("task", Enum("text-to-video", "image-to-video")), ("model", Text()),
            ("output_indices", Obj(("type", S("array")), ("minItems", N(1)), ("maxItems", N(1)), ("items", Obj(("const", N(1)))))),
            ("provider_context", Obj(("type", S("object")), ("additionalProperties", B(false)))))));

    public static JsonObject Effective(string task, JsonObject publicSchema)
    {
        var result = publicSchema.DeepClone().AsObject();
        result["required"] = Strings("schema_version", "request_id", "task", "model", "prompt", "parameters", "attachments", "provider_parameters", "execution");
        var properties = result["properties"]!.AsObject();
        properties["task"] = Constant(task);
        properties["model"] = Text();
        properties["prompt"] = Text();
        properties["parameters"] = Obj(("type", S("object")), ("additionalProperties", B(false)),
            ("required", Strings("size", "duration", "num_videos")), ("properties", Obj(("size", Enum(OpenAiVideoMetadata.Sizes)),
                ("duration", Obj(("type", S("integer")), ("enum", new JsonArray(N(4), N(8), N(12))))), ("num_videos", Obj(("const", N(1)))))));
        properties["provider_parameters"] = Obj(("type", S("object")), ("additionalProperties", B(false)));
        var attachments = properties["attachments"]!.AsObject();
        attachments["minItems"] = N(task == "image-to-video" ? 1 : 0);
        attachments["maxItems"] = N(task == "image-to-video" ? 1 : 0);
        var item = attachments["items"]!["properties"]!;
        item["role"] = Constant("reference");
        item["source"] = Obj(("type", S("object")), ("additionalProperties", B(false)), ("required", Strings("path")), ("properties", Obj(("path", Text()))));
        return result;
    }

    public static JsonArray Conversions()
    {
        var dimensions = Obj(("type", S("object")), ("required", Strings("parameters")),
            ("properties", Obj(("parameters", Obj(("type", S("object")), ("required", Strings("width", "height")),
                ("properties", Obj(("width", Integer()), ("height", Integer()))))))));
        var attachment = Obj(("type", S("object")), ("required", Strings("role")),
            ("properties", Obj(("role", Enum("reference", "source", "first_frame", "last_frame")))));
        var frames = Obj(("type", S("object")), ("required", Strings("attachments")),
            ("properties", Obj(("attachments", Obj(("type", S("array")), ("minItems", N(1)), ("items", attachment))))));
        return new JsonArray(
            Obj(("id", S("dimensions_to_size")), ("source_parameters", Strings("/parameters/width", "/parameters/height")),
                ("target_parameters", Strings("/parameters/size")), ("description", S("正整数像素宽高成对，精确转为 widthxheight；显式 size 须相同。")), ("source_schema", dimensions)),
            Obj(("id", S("first_frame_to_reference")), ("source_parameters", Strings("/attachments/*/role")), ("target_parameters", Strings("/attachments/*/role")),
                ("description", S("首帧图等价映射 input_reference；仅 image-to-video 单张本地图片，不缩放、不减少附件。")), ("source_schema", frames)));
    }
}
