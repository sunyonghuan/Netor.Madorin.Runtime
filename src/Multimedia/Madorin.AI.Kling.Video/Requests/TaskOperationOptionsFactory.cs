using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Video.Metadata;

namespace Madorin.AI.Kling.Video.Requests;

internal static class TaskOperationOptionsFactory
{
    public static async Task<TaskOperationOptions> CreateAsync(string[] args, bool download, WorkspacePaths paths, SkillInvocation invocation)
    {
        var allowed = new HashSet<string>(["request-id", "task-id", "api-url", "api-key", "timeout", "output-format", "verbose", "wait", "output-dir", "task-context-file"], StringComparer.Ordinal);
        var options = CommandLineParser.Parse(args, allowed, new HashSet<string>(["verbose", "wait"], StringComparer.Ordinal));
        if (download && options.ContainsKey("wait") || !download && options.ContainsKey("output-dir")) throw new SkillException("invalid_parameter", "选项不适用于当前命令。");
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(options.GetValueOrDefault("api-key"));
        invocation.SetTimeout(CommandLineParser.ParsePositiveInt(options.GetValueOrDefault("timeout"), 300, "timeout"));
        var id = options.GetValueOrDefault("request-id");
        var taskId = options.GetValueOrDefault("task-id");
        if (!RequestIds.IsValidRequestId(id) || !KlingTaskIds.IsValid(taskId) || options.TryGetValue("output-format", out var format) && format != "json")
        {
            throw new SkillException("invalid_parameter", "必须提供合法的 request-id 与 task-id，输出格式仅支持 json。");
        }

        invocation.ResponseId = id;
        invocation.TaskId = taskId;
        invocation.RemoteCancellation = "unsupported";
        var apiKey = options.GetValueOrDefault("api-key");
        var url = options.GetValueOrDefault("api-url") ?? KlingVideoMetadata.DefaultApiUrl;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || !VideoGenerationOptionsFactory.TryEndpoint(url, out var endpoint))
        {
            throw new SkillException("invalid_parameter", "必须提供 api-key 和有效 HTTPS API 地址。");
        }

        if (!options.TryGetValue("task-context-file", out var contextFile) || string.IsNullOrWhiteSpace(contextFile))
        {
            throw new SkillException("invalid_parameter", "查询和下载必须提供 task-context-file。");
        }

        var context = await KlingVideoTaskContext.ReadAsync(contextFile, taskId!, paths, invocation.CancellationToken);
        invocation.TaskContext = context;
        invocation.Model = context["model"]!.GetValue<string>();
        string? output = null;
        if (download)
        {
            var raw = options.GetValueOrDefault("output-dir");
            if (string.IsNullOrWhiteSpace(raw)) throw new SkillException("invalid_parameter", "下载必须指定 output-dir。");
            output = paths.Resolve(raw);
        }

        return new TaskOperationOptions(id!, taskId!, apiKey, endpoint!, output, options.GetValueOrDefault("wait") == "true", CommandLineParser.ParsePositiveInt(options.GetValueOrDefault("timeout"), 300, "timeout"), invocation.Model!);
    }
}
