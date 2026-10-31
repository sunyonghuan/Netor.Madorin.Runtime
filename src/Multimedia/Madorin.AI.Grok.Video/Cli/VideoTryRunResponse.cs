using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Requests;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Video.Cli;

internal static class VideoTryRunResponse
{
    public static JsonObject Build(VideoGenerationOptions options, SkillInvocation invocation, JsonObject preview)
    {
        var execution = Obj(
            ("timeout", N(options.TimeoutSeconds)), ("try_run", B(options.TryRun)), ("verbose", B(options.Verbose)),
            ("no_wait", B(options.NoWait)), ("output_format", S("json")));
        if (options.OutputDirectory is not null)
        {
            execution["output_dir"] = options.InputParameters["execution"]?["output_dir"]?.DeepClone();
        }

        var effective = Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("request_id", S(options.RequestId)),
            ("prompt", S(options.Prompt)), ("task", S(options.Task)), ("model", S(options.Model)),
            ("parameters", Obj(("duration", N(options.DurationSeconds)), ("aspect_ratio", S(options.AspectRatio)), ("resolution", S(options.Resolution)))),
            ("attachments", new JsonArray()), ("provider_parameters", new JsonObject()), ("execution", execution));
        var input = VideoSafeEcho.Sanitize(options.InputParameters);
        return Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(options.RequestId)), ("status", S("validated")),
            ("mode", S("try_run")), ("provider", S("grok")), ("model", S(options.Model)), ("input_parameters", input),
            ("effective_parameters", VideoSafeEcho.Sanitize(effective)), ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))),
            ("request_preview", VideoSafeEcho.Sanitize(preview)), ("unicode_checks", VideoSafeEcho.UnicodeChecks(input)),
            ("errors", new JsonArray()), ("ignored_parameters", invocation.IgnoredParameters.DeepClone()),
            ("converted_parameters", invocation.ConvertedParameters.DeepClone()), ("side_effects", SkillResponses.NoSideEffects()));
    }
}
