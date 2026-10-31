using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Video.Requests;

internal static class VideoInputEcho
{
    public static JsonObject Build(Dictionary<string, string?> direct, VideoRequestFile? file)
    {
        var input = file?.Content.DeepClone().AsObject() ?? new JsonObject();
        var parameters = input["parameters"]?.AsObject() ?? new JsonObject();
        var execution = input["execution"]?.AsObject() ?? new JsonObject();
        var providers = input["provider_parameters"]?.AsObject() ?? new JsonObject();

        foreach (var pair in direct)
        {
            var name = pair.Key.Replace('-', '_');
            switch (name)
            {
                case "api_key":
                    input[name] = S("<redacted>");
                    break;
                case "request_id":
                    input[name] = S(pair.Value);
                    break;
                case "request_file" or "api_url":
                    input[name] = S(pair.Value);
                    break;
                case "try_run" or "verbose" or "no_wait":
                    execution[name] = B(pair.Value == "true");
                    break;
                case "timeout":
                    execution[name] = N(ParsePositiveInt(pair.Value, 300, name));
                    break;
                case "duration":
                    parameters[name] = N(ParsePositiveInt(pair.Value, 8, name));
                    break;
                case "aspect_ratio" or "resolution":
                    parameters[name] = S(pair.Value);
                    break;
                case "output_format" or "output_dir":
                    execution[name] = S(pair.Value);
                    break;
                case "prompt" or "task" or "model":
                    input[name] = S(pair.Value);
                    break;
                default:
                    parameters[name] = S(pair.Value);
                    break;
            }
        }

        if (input["parameters"] is null)
        {
            input["parameters"] = parameters;
        }

        if (input["execution"] is null)
        {
            input["execution"] = execution;
        }

        if (providers.Count > 0 && input["provider_parameters"] is null)
        {
            input["provider_parameters"] = providers;
        }

        return input;
    }
}
