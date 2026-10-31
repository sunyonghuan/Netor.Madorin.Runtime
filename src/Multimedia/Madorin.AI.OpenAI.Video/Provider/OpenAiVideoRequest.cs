using System.Globalization;
using System.Text.Json.Nodes;
using Madorin.AI.OpenAI.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Video.Provider;

internal sealed record OpenAiVideoRequest(string Model, string Prompt, string Seconds, string Size, bool HasReference)
{
    public static OpenAiVideoRequest Map(VideoGenerationOptions options) => new(options.Model, options.Prompt,
        options.Duration.ToString(CultureInfo.InvariantCulture), options.Size, options.ReferencePath is not null);

    public JsonObject Preview() => Obj(("model", S(Model)), ("prompt", S(Prompt)), ("seconds", S(Seconds)), ("size", S(Size)),
        ("input_reference", HasReference ? S("<multipart file: reference>") : null));
}
