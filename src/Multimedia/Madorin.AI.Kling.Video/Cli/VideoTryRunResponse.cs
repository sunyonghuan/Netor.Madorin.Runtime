using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Video.Cli;

internal static class VideoTryRunResponse
{
    public static JsonObject Build(VideoGenerationOptions options, SkillInvocation invocation, JsonObject preview)
    {
        var parameters = Obj(("resolution", S(options.Resolution)));
        if (options.Duration is not null) parameters["duration"] = N(options.Duration.Value);
        var kling = new JsonObject();
        if (options.AspectRatio is not null) kling["aspect_ratio"] = S(options.AspectRatio);
        if (options.WatermarkEnabled is not null) kling["watermark_enabled"] = B(options.WatermarkEnabled.Value);
        var execution = Obj(("timeout", N(options.TimeoutSeconds)), ("try_run", B(options.TryRun)), ("verbose", B(options.Verbose)), ("no_wait", B(options.NoWait)), ("output_format", S("json")));
        if (options.OutputDirectory is not null) execution["output_dir"] = options.InputParameters["execution"]?["output_dir"]?.DeepClone();
        var effective = Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("request_id", S(options.RequestId)), ("prompt", options.Prompt is null ? null : S(options.Prompt)),
            ("task", S(options.Task)), ("model", S(options.Model)), ("parameters", parameters),
            ("attachments", options.InputParameters["attachments"]?.DeepClone() ?? new JsonArray()),
            ("provider_parameters", Obj(("kling", kling))), ("execution", execution));
        return Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(options.RequestId)), ("status", S("validated")),
            ("mode", S("try_run")), ("provider", S("kling")), ("model", S(options.Model)), ("input_parameters", options.InputParameters),
            ("effective_parameters", effective), ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))),
            ("request_preview", preview), ("unicode_checks", SkillResponses.UnicodeChecks(options.InputParameters)), ("errors", new JsonArray()),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()),
            ("side_effects", SkillResponses.NoSideEffects()));
    }
}
