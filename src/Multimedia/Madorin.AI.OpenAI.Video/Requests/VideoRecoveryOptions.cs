using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.OpenAI.Video.Requests;

internal sealed record VideoRecoveryOptions(ModelListOptions Connection, string TaskId, string? OutputDirectory, bool Wait)
{
    public static async Task<VideoRecoveryOptions> ParseAsync(string command, string[] args, WorkspacePaths paths, SkillInvocation invocation)
    {
        var names = new HashSet<string>(["request-id", "api-url", "api-key", "task-id", "task-context-file", "timeout", "output-format", "verbose"], StringComparer.Ordinal);
        if (command == "query") { names.Add("wait"); }
        else { names.Add("output-dir"); }
        var direct = CommandLineParser.Parse(args, names, new HashSet<string>(["verbose", "wait"], StringComparer.Ordinal));
        invocation.Verbose = direct.GetValueOrDefault("verbose") == "true";
        var timeout = CommandLineParser.ParsePositiveInt(direct.GetValueOrDefault("timeout"), 300, "timeout");
        invocation.SetTimeout(timeout);
        var id = direct.GetValueOrDefault("task-id");
        if (!VideoTaskContext.IsValidTaskId(id)) { throw new SkillException("invalid_parameter", "必须提供合法 task-id。"); }
        invocation.TaskId = id;
        var file = direct.GetValueOrDefault("task-context-file");
        if (string.IsNullOrWhiteSpace(file)) { throw new SkillException("invalid_parameter", "query/download 必须提供 task-context-file。"); }
        var context = await VideoTaskContext.ReadAsync(file, id!, paths, invocation.CancellationToken);
        invocation.TaskContext = context;
        invocation.Model = context["model"]!.GetValue<string>();
        invocation.RemoteCancellation = "not_requested";
        var connectionArgs = new List<string>();
        foreach (var key in new[] { "request-id", "api-url", "api-key", "timeout", "output-format" })
        {
            if (direct.TryGetValue(key, out var value)) { connectionArgs.Add("--" + key); connectionArgs.Add(value!); }
        }
        var connection = ModelListOptions.Parse(connectionArgs.ToArray(), "https://api.openai.com/v1", invocation);
        invocation.Verbose = direct.GetValueOrDefault("verbose") == "true";
        string? output = null;
        if (command == "download")
        {
            if (!direct.TryGetValue("output-dir", out var directory) || string.IsNullOrWhiteSpace(directory))
            {
                throw new SkillException("invalid_parameter", "download 必须提供 output-dir。");
            }
            output = paths.Resolve(directory);
        }
        return new(connection, id!, output, direct.GetValueOrDefault("wait") == "true");
    }
}
