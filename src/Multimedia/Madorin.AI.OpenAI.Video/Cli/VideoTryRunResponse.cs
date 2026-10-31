using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Video.Provider;
using Madorin.AI.OpenAI.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Video.Cli;

internal static class VideoTryRunResponse
{
    public static JsonObject Build(VideoGenerationOptions options, SkillInvocation invocation, OpenAiVideoRequest request)
    {
        var input = options.InputParameters.DeepClone().AsObject();
        // URI 是可能的秘密载体；仅保留已采用的本地附件，不回显被过滤 URI 或未知参数值。
        var attachments = new JsonArray();
        if (options.ReferencePath is not null && input["attachments"] is JsonArray source)
        {
            foreach (var item in source.Where(item => item?["role"]?.GetValue<string>() is "reference" or "first_frame")) { attachments.Add(item!.DeepClone()); }
        }
        input["attachments"] = attachments.DeepClone();
        var effectiveAttachments = attachments.DeepClone().AsArray();
        foreach (var item in effectiveAttachments) { item!["role"] = S("reference"); }
        var effective = Obj(("schema_version", S("1.0")), ("request_id", S(options.RequestId)), ("task", S(options.Task)),
            ("model", S(options.Model)), ("prompt", S(options.Prompt)), ("attachments", effectiveAttachments), ("provider_parameters", new JsonObject()),
            ("parameters", Obj(("size", S(options.Size)), ("duration", N(options.Duration)), ("num_videos", N(1)))),
            ("execution", Obj(("timeout", N(options.Timeout)), ("try_run", B(options.TryRun)), ("verbose", B(options.Verbose)),
                ("no_wait", B(options.NoWait)), ("output_format", S("json")))));
        if (input["execution"]?["output_dir"] is { } directory) { effective["execution"]!["output_dir"] = directory.DeepClone(); }
        return Obj(("schema_version", S("1.0")), ("response_id", S(options.RequestId)), ("status", S("validated")), ("mode", S("try_run")),
            ("provider", S("openai")), ("model", S(options.Model)), ("input_parameters", input), ("effective_parameters", effective),
            ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))), ("request_preview", request.Preview()),
            ("unicode_checks", SkillResponses.UnicodeChecks(input)), ("errors", new JsonArray()),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()),
            ("side_effects", SkillResponses.NoSideEffects()));
    }
}
