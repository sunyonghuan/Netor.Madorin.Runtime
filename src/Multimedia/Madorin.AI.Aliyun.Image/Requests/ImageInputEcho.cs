using System.Text.Json.Nodes;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
namespace Madorin.AI.Aliyun.Image.Requests;
internal static class ImageInputEcho
{
    public static JsonObject Build(Dictionary<string, string?> direct, ImageRequestFile? file)
    {
        var input = file?.Content.DeepClone().AsObject() ?? new JsonObject(); var parameters = input["parameters"]?.AsObject() ?? new JsonObject(); var execution = input["execution"]?.AsObject() ?? new JsonObject();
        foreach (var pair in direct)
        {
            var name = pair.Key.Replace('-', '_');
            switch (name)
            {
                case "api_key": input[name] = S("<redacted>"); break;
                case "request_id": input[name] = S(pair.Value); break;
                case "request_file" or "api_url": input[name] = S(pair.Value); break;
                case "try_run" or "verbose" or "no_wait": execution[name] = B(pair.Value == "true"); break;
                case "timeout": execution[name] = N(ParsePositiveInt(pair.Value, 300, name)); break;
                case "output_format" or "output_dir": execution[name] = S(pair.Value); break;
                case "prompt" or "task" or "model": input[name] = S(pair.Value); break;
                case "num_images": parameters[name] = N(ParsePositiveInt(pair.Value, 1, name)); break;
                case "provider_aliyun_seed": parameters["seed"] = N(ParseNonNegativeInt(pair.Value, name)); break;
                case "provider_aliyun_prompt_extend": parameters["prompt_extend"] = B(ParseBoolean(pair.Value, name)); break;
                case "provider_aliyun_watermark": parameters["watermark"] = B(ParseBoolean(pair.Value, name)); break;
                default: parameters[name] = S(pair.Value); break;
            }
        }
        if (input["parameters"] is null) input["parameters"] = parameters;
        if (input["execution"] is null) input["execution"] = execution;

        static int ParseNonNegativeInt(string? value, string name)
        {
            if (!int.TryParse(value, out var parsed) || parsed < 0) throw new SkillException("invalid_parameter", $"{name} 必须是非负整数。");
            return parsed;
        }

        static bool ParseBoolean(string? value, string name)
        {
            if (value is "true") return true;
            if (value is "false") return false;
            throw new SkillException("invalid_parameter", $"{name} 必须是 true 或 false。");
        }
        return input;
    }
}
