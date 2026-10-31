using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Files;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;

namespace Madorin.AI.OpenAI.Video.Requests;

internal static class VideoInputEcho
{
    public static JsonObject Build(Dictionary<string, string?> direct, GenerationRequestFile? file)
    {
        var input = file?.Content.DeepClone().AsObject() ?? new JsonObject();
        var parameters = input["parameters"]?.AsObject() ?? new JsonObject();
        var execution = input["execution"]?.AsObject() ?? new JsonObject();
        foreach (var pair in direct)
        {
            var name = pair.Key.Replace('-', '_');
            switch (name)
            {
                case "api_key": input[name] = S("<redacted>"); break;
                case "try_run" or "verbose" or "no_wait": execution[name] = B(pair.Value == "true"); break;
                case "timeout": execution[name] = N(ParsePositiveInt(pair.Value, 300, name)); break;
                case "width" or "height" or "duration" or "num_videos": parameters[name] = N(ParsePositiveInt(pair.Value, 1, name)); break;
                case "output_format" or "output_dir": execution[name] = S(pair.Value); break;
                case "prompt" or "task" or "model" or "request_id" or "request_file" or "api_url": input[name] = S(pair.Value); break;
                default: parameters[name] = S(pair.Value); break;
            }
        }
        if (input["parameters"] is null) { input["parameters"] = parameters; }
        if (input["execution"] is null) { input["execution"] = execution; }
        return input;
    }
}
