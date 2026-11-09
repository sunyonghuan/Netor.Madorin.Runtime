using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Aliyun.Image.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Aliyun.Image.Cli;

internal static class ImageTryRunResponse
{
    public static JsonObject Build(ImageGenerationOptions options, SkillInvocation invocation, JsonObject preview)
    {
        var execution = Obj(("timeout", N(options.TimeoutSeconds)), ("try_run", B(options.TryRun)), ("verbose", B(options.Verbose)),
            ("no_wait", B(false)), ("output_format", S("json")));
        if (options.OutputDirectory is not null)
        {
            // 回显调用方原始写法，不暴露内部解析得到的绝对路径。
            execution["output_dir"] = options.InputParameters["execution"]?["output_dir"]?.DeepClone();
        }

        var effective = Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("request_id", S(options.RequestId)),
            ("prompt", S(options.Prompt)), ("task", S(options.Task)), ("model", S(options.Model)),
            ("parameters", Obj(("size", S(options.Size!)), ("num_images", N(options.ImageCount)), ("negative_prompt", S(options.NegativePrompt)), ("prompt_extend", B(options.PromptExtend ?? true)), ("watermark", B(options.Watermark ?? false)), ("seed", options.Seed is null ? null : N(options.Seed.Value))),
            ("attachments", new JsonArray()), ("provider_parameters", new JsonObject()),
            ("execution", execution));
        return Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(options.RequestId)), ("status", S("validated")),
            ("mode", S("try_run")), ("provider", S("aliyun")), ("model", S(options.Model)), ("input_parameters", options.InputParameters),
            ("effective_parameters", effective), ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))),
            ("request_preview", preview), ("unicode_checks", SkillResponses.UnicodeChecks(options.InputParameters)),
            ("errors", new JsonArray()), ("ignored_parameters", invocation.IgnoredParameters.DeepClone()),
            ("converted_parameters", invocation.ConvertedParameters.DeepClone()), ("side_effects", SkillResponses.NoSideEffects()));
    }
}




