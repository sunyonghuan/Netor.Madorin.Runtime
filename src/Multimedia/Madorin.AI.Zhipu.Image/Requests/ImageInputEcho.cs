using System.Globalization;
using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Image.Requests;

internal static class ImageInputEcho
{
    public static JsonObject Build(Dictionary<string, string?> direct, ImageRequestFile? file)
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
                case "api_key" or "request_file" or "api_url":
                    break;
                case "request_id" or "prompt" or "task" or "model":
                    input[name] = S(pair.Value);
                    break;
                case "try_run" or "verbose" or "no_wait":
                    execution[name] = B(pair.Value == "true");
                    break;
                case "timeout":
                    execution[name] = N(ParsePositiveInt(pair.Value, 300, name));
                    break;
                case "output_format" or "output_dir":
                    execution[name] = S(pair.Value);
                    break;
                case "width" or "height":
                    parameters[name] = N(long.Parse(pair.Value!, CultureInfo.InvariantCulture));
                    break;
                case "size" or "quality":
                    parameters[name] = S(pair.Value);
                    break;
                case "provider_zhipu_watermark":
                    Zhipu(providers)["watermark_enabled"] = B(pair.Value == "true");
                    break;
                case "provider_zhipu_user_id":
                    Zhipu(providers)["user_id"] = S(pair.Value);
                    break;
            }
        }

        input["parameters"] = parameters;
        input["execution"] = execution;
        if (providers.Count > 0) input["provider_parameters"] = providers;
        return input;
    }

    private static JsonObject Zhipu(JsonObject providers)
    {
        if (providers["zhipu"] is not JsonObject zhipu)
        {
            zhipu = new JsonObject();
            providers["zhipu"] = zhipu;
        }

        return zhipu;
    }
}
