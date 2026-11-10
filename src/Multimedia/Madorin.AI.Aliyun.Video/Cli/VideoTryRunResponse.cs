using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Aliyun.Video.Provider;
using Madorin.AI.Aliyun.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Aliyun.Video.Cli;

internal static class VideoTryRunResponse
{
    public static JsonObject Build(VideoGenerationOptions options, SkillInvocation invocation, JsonObject preview)
    {
        var parameters = Obj(("duration", N(options.Duration)), ("resolution", S(options.Resolution)));
        if (options.AspectRatio is not null) parameters["aspect_ratio"] = S(options.AspectRatio);
        if (options.NegativePrompt is not null) parameters["negative_prompt"] = S(options.NegativePrompt);
        var aliyun = Obj(("prompt_extend", B(options.PromptExtend)), ("watermark", B(options.Watermark)));
        if (options.Seed is not null) aliyun["seed"] = N(options.Seed.Value);
        if (options.AudioUrl is not null) aliyun["audio_url"] = S(options.AudioUrl);
        var execution = Obj(("timeout", N(options.TimeoutSeconds)), ("try_run", B(true)), ("verbose", B(options.Verbose)), ("no_wait", B(options.NoWait)), ("output_format", S("json")));
        if (options.OutputDirectory is not null) execution["output_dir"] = options.InputParameters["execution"]?["output_dir"]?.DeepClone();
        var effective = Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("request_id", S(options.RequestId)), ("prompt", options.Prompt is null ? null : S(options.Prompt)),
            ("task", S(options.Task)), ("model", S(options.Model)), ("parameters", parameters),
            ("attachments", options.InputParameters["attachments"]?.DeepClone() ?? new JsonArray()),
            ("provider_parameters", new JsonObject { ["aliyun"] = aliyun }), ("execution", execution));
        var input = AliyunVideoClient.Redact(options.InputParameters.DeepClone());
        AliyunVideoClient.Redact(effective);
        return Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(options.RequestId)), ("status", S("validated")), ("mode", S("try_run")),
            ("provider", S("aliyun")), ("model", S(options.Model)), ("input_parameters", input), ("effective_parameters", effective),
            ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))), ("request_preview", preview),
            ("unicode_checks", SkillResponses.UnicodeChecks(input)), ("errors", new JsonArray()),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()),
            ("side_effects", SkillResponses.NoSideEffects()));
    }
}
