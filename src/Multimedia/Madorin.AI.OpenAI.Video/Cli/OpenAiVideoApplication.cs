using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Video.Metadata;
using Madorin.AI.OpenAI.Video.Provider;
using Madorin.AI.OpenAI.Video.Requests;

namespace Madorin.AI.OpenAI.Video.Cli;

internal sealed class OpenAiVideoApplication(HttpClient client)
{
    public async Task<int> RunAsync(string[] args, TextWriter output, string workingDirectory, Stream? controlInput = null, TextWriter? diagnostics = null)
    {
        var writer = new ProtocolWriter(output);
        var paths = new WorkspacePaths(workingDirectory);
        await using var invocation = new SkillInvocation();
        using var artifacts = new VideoArtifactStore(paths);
        var id = RequestIds.FindRequestId(args);
        invocation.ResponseId = RequestIds.IsValidRequestId(id) ? id : null;
        invocation.ListenForCancellation(controlInput);
        invocation.TryRun = args.Length > 0 && args[0] == "generate" && args.Any(value => value is "--try-run" or "--try-run=true");
        var provider = new OpenAiVideoClient(client, diagnostics);
        var exit = 0;
        try
        {
            if (args.Length == 0 || id is not null && !RequestIds.IsValidRequestId(id))
            {
                throw new SkillException("invalid_parameter", "命令缺失或 request-id 无效。");
            }
            JsonObject response;
            switch (args[0])
            {
                case "generate":
                    var options = await new VideoGenerationOptionsFactory(paths).CreateAsync(args[1..], invocation);
                    var mapped = OpenAiVideoRequest.Map(options);
                    if (options.TryRun) { response = VideoTryRunResponse.Build(options, invocation, mapped); break; }
                    var job = await provider.SubmitAsync(options, mapped, paths, invocation);
                    if (options.NoWait)
                    {
                        response = VideoTaskResponse.Build(job, invocation, true);
                        break;
                    }
                    job = await provider.WaitAsync(job, new(options.Endpoint, options.ApiKey), invocation);
                    VideoTaskResponse.RequireCompleted(job);
                    response = SkillResponses.Generated("openai", "video", invocation,
                        await provider.DownloadAsync(new(options.Endpoint, options.ApiKey), options.OutputDirectory!, artifacts, invocation));
                    break;
                case "query" or "download":
                    var recovery = await VideoRecoveryOptions.ParseAsync(args[0], args[1..], paths, invocation);
                    var observed = await provider.RetrieveAsync(recovery.Connection, invocation);
                    if (recovery.Wait) { observed = await provider.WaitAsync(observed, recovery.Connection, invocation); }
                    if (args[0] == "query")
                    {
                        response = VideoTaskResponse.Build(observed, invocation, false);
                    }
                    else
                    {
                        VideoTaskResponse.RequireCompleted(observed);
                        response = SkillResponses.Generated("openai", "video", invocation,
                            await provider.DownloadAsync(recovery.Connection, recovery.OutputDirectory!, artifacts, invocation));
                    }
                    break;
                case "list-models":
                    response = await provider.ListModelsAsync(ModelListOptions.Parse(args[1..], "https://api.openai.com/v1", invocation), invocation);
                    break;
                case "metadata" or "version":
                    var local = CommandLineParser.Parse(args[1..], new HashSet<string>(["request-id", "output-format", "verbose"], StringComparer.Ordinal),
                        new HashSet<string>(["verbose"], StringComparer.Ordinal));
                    if (id is null || local.TryGetValue("output-format", out var format) && format != "json")
                    {
                        throw new SkillException("invalid_parameter", "本地命令需 request-id，格式仅支持 json。");
                    }
                    response = args[0] == "metadata" ? OpenAiVideoMetadata.Build(id) : OpenAiVideoMetadata.Version(id);
                    break;
                default: throw new SkillException("invalid_parameter", "未知命令。");
            }
            if (response["status"]?.GetValue<string>() is "failed" or "cancelled")
            {
                exit = ProtocolWriter.ExitCode(response["error"]!["code"]!.GetValue<string>());
            }
            if (invocation.CommitTerminal() is { } termination) { return Fail(termination, string.Empty); }
            var result = writer.Write(response, exit);
            artifacts.Commit();
            return result;
        }
        catch (SkillException exception)
        {
            var code = invocation.CommitTerminal(exception.Code)!;
            if (invocation.TryRun && code == "invalid_parameter") { return writer.Write(SkillResponses.Invalid("openai", invocation, code, exception.Message), 1); }
            return Fail(code, exception.Message, exception.ProviderCode);
        }
        catch (OperationCanceledException) { return Fail("timeout", string.Empty); }
        catch (HttpRequestException) { return Fail("network_error", "连接 OpenAI 视频服务失败。"); }
        catch (Exception) { return Fail("internal_error", "发生未预期错误。"); }

        int Fail(string code, string message, string? providerCode = null)
        {
            var final = invocation.CommitTerminal(code)!;
            if (invocation.TaskId is not null && args[0] == "generate" && final is "timeout" or "cancelled")
            {
                invocation.RemoteCancellation = "unsupported";
            }
            return writer.Write(SkillResponses.Failure("openai", invocation, final,
                final == "timeout" ? "调用超时。" : final == "cancelled" ? "调用已取消。" : message, final == code ? providerCode : null), ProtocolWriter.ExitCode(final));
        }
    }
}
