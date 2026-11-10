using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Video.Cli;

internal static class VideoTryRunResponse
{
    public static JsonObject Build(VideoGenerationOptions options, SkillInvocation invocation, JsonObject preview)
    {
        var parameters = Obj(("duration", N(options.Duration)), ("fps", N(options.Fps)));
        if (options.Size is not null) parameters["size"] = S(options.Size);
        var zhipu = new JsonObject();
        if (options.Quality is not null) zhipu["quality"] = S(options.Quality);
        if (options.WithAudio is not null) zhipu["with_audio"] = B(options.WithAudio.Value);
        if (options.WatermarkEnabled is not null) zhipu["watermark_enabled"] = B(options.WatermarkEnabled.Value);
        if (options.UserId is not null) zhipu["user_id"] = S(options.UserId);
        var provider = zhipu.Count == 0 ? new JsonObject() : new JsonObject { ["zhipu"] = zhipu };
        var execution = Obj(("timeout", N(options.TimeoutSeconds)), ("try_run", B(true)), ("verbose", B(options.Verbose)), ("no_wait", B(options.NoWait)), ("output_format", S("json")));
        if (options.OutputDirectory is not null) execution["output_dir"] = options.InputParameters["execution"]?["output_dir"]?.DeepClone();
        var effective = Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("request_id", S(options.RequestId)), ("prompt", options.Prompt is null ? null : S(options.Prompt)),
            ("task", S(options.Task)), ("model", S(options.Model)), ("parameters", parameters),
            ("attachments", options.InputParameters["attachments"]?.DeepClone() ?? new JsonArray()), ("provider_parameters", provider), ("execution", execution));
        return Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(options.RequestId)), ("status", S("validated")), ("mode", S("try_run")),
            ("provider", S("zhipu")), ("model", S(options.Model)), ("input_parameters", options.InputParameters), ("effective_parameters", effective),
            ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))), ("request_preview", preview),
            ("unicode_checks", SkillResponses.UnicodeChecks(options.InputParameters)), ("errors", new JsonArray()),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()),
            ("side_effects", SkillResponses.NoSideEffects()));
    }
}
