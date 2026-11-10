using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Image.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Image.Cli;

internal static class ImageTryRunResponse
{
    public static JsonObject Build(ImageGenerationOptions options, SkillInvocation invocation, JsonObject preview)
    {
        var parameters = Obj(("size", S(options.Size)), ("quality", S(options.Quality)));
        var provider = new JsonObject();
        if (options.WatermarkEnabled is not null || options.UserId is not null)
        {
            var zhipu = new JsonObject();
            if (options.WatermarkEnabled is not null) zhipu["watermark_enabled"] = B(options.WatermarkEnabled.Value);
            if (options.UserId is not null) zhipu["user_id"] = S(options.UserId);
            provider["zhipu"] = zhipu;
        }

        var execution = Obj(("timeout", N(options.TimeoutSeconds)), ("try_run", B(options.TryRun)), ("verbose", B(options.Verbose)), ("no_wait", B(false)), ("output_format", S("json")));
        if (options.OutputDirectory is not null) execution["output_dir"] = options.InputParameters["execution"]?["output_dir"]?.DeepClone();
        var effective = Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("request_id", S(options.RequestId)), ("prompt", S(options.Prompt)),
            ("task", S(options.Task)), ("model", S(options.Model)), ("parameters", parameters), ("attachments", new JsonArray()),
            ("provider_parameters", provider), ("execution", execution));
        return Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(options.RequestId)), ("status", S("validated")),
            ("mode", S("try_run")), ("provider", S("zhipu")), ("model", S(options.Model)), ("input_parameters", options.InputParameters),
            ("effective_parameters", effective), ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))),
            ("request_preview", preview), ("unicode_checks", SkillResponses.UnicodeChecks(options.InputParameters)), ("errors", new JsonArray()),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()),
            ("side_effects", SkillResponses.NoSideEffects()));
    }
}
