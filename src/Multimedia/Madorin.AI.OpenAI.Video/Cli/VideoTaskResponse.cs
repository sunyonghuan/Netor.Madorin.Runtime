using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Video.Provider;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.OpenAI.Video.Cli;

internal static class VideoTaskResponse
{
    public static JsonObject Build(OpenAiVideoJob job, SkillInvocation invocation, bool submitted)
    {
        var status = job.Status switch
        {
            "queued" => submitted ? "submitted" : "pending",
            "in_progress" => submitted ? "submitted" : "running",
            "completed" => submitted ? "submitted" : "succeeded",
            "cancelled" or "canceled" => "cancelled",
            _ => "failed"
        };
        var error = status switch
        {
            "failed" or "expired" => Error(OpenAiVideoClient.ErrorCode(job.Error?.Code), job.Error?.Code),
            "cancelled" => Error("cancelled", null),
            _ => null
        };
        return Obj(("schema_version", S("1.0")), ("response_id", S(invocation.ResponseId)), ("status", S(status)),
            ("task_id", S(invocation.TaskId)), ("task_context", invocation.TaskContext!.DeepClone()),
            ("provider_metadata", SkillResponses.ProviderMetadata("openai", invocation)), ("error", error),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));
    }
    public static void RequireCompleted(OpenAiVideoJob job)
    {
        if (job.Status is "cancelled" or "canceled") { throw new SkillException("cancelled", "远端视频任务已取消。"); }
        if (job.Status is "failed" or "expired") { throw new SkillException(OpenAiVideoClient.ErrorCode(job.Error?.Code), "远端视频任务失败或已过期。"); }
        if (job.Status != "completed") { throw new SkillException("task_not_ready", "视频任务尚未完成。"); }
    }
    private static JsonObject Error(string code, string? providerCode)
    {
        JsonNode? providerError = null;
        if (IsSafeProviderCode(providerCode))
        {
            providerError = Obj(("code", S(providerCode)), ("message", S("Provider 视频任务报告失败。")));
        }
        return Obj(("code", S(code)), ("message", S(code == "cancelled" ? "远端视频任务已取消。" : "远端视频任务失败。")),
            ("provider_error", providerError));
    }

    private static bool IsSafeProviderCode(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
