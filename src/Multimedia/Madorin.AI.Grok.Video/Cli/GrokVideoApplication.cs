using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Metadata;
using Madorin.AI.Grok.Video.Provider;
using Madorin.AI.Grok.Video.Requests;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Grok.Video.Cli;

internal sealed class GrokVideoApplication(HttpClient client, TimeSpan? pollInterval = null)
{
    public async Task<int> RunAsync(string[] args, TextWriter output, string workingDirectory, Stream? controlInput = null)
    {
        var writer = new ProtocolWriter(output);
        var paths = new WorkspacePaths(workingDirectory);
        await using var invocation = new SkillInvocation();
        using var artifacts = new VideoArtifactStore(paths);
        var id = RequestIds.FindRequestId(args);
        invocation.ResponseId = RequestIds.IsValidRequestId(id) ? id : null;
        if (args.Length > 0 && args[0] is "query" or "download")
        {
            invocation.TaskId = GrokVideoTaskIds.Find(args[1..]);
        }
        invocation.ListenForCancellation(controlInput);
        invocation.TryRun = args.Length > 0 && args[0] == "generate" && args.Any(value => value is "--try-run" or "--try-run=true");
        try
        {
            if (args.Length == 0)
            {
                throw new SkillException("invalid_parameter", "命令必需。支持 metadata、list-models、version、generate、query、download。");
            }

            if (id is not null && !RequestIds.IsValidRequestId(id))
            {
                throw new SkillException("invalid_parameter", "request-id 格式无效。");
            }

            JsonObject response = args[0] switch
            {
                "generate" => await GenerateAsync(args, invocation, paths, artifacts),
                "list-models" => await ListAsync(args, invocation),
                "query" => await QueryAsync(args, invocation, paths),
                "download" => await DownloadAsync(args, invocation, paths, artifacts),
                _ => Local(args, id)
            };

            if (invocation.CommitTerminal() is { } termination)
            {
                return WriteFailure(termination, string.Empty);
            }

            var exitCode = StatusExit(response, writer);
            artifacts.Commit();
            return exitCode;
        }
        catch (SkillException exception)
        {
            var code = invocation.CommitTerminal(exception.Code);
            var providerCode = GrokVideoErrors.SafeCode(exception.ProviderCode, "");
            if (invocation.TryRun && code == "invalid_parameter")
            {
                return writer.Write(SkillResponses.Invalid("grok", invocation, exception.Code, exception.Message), 1);
            }

            return WriteFailure(code!, exception.Message, providerCode);
        }
        catch (OperationCanceledException)
        {
            return WriteFailure("timeout", string.Empty);
        }
        catch (HttpRequestException)
        {
            return WriteFailure("network_error", "连接 Grok 失败。");
        }
        catch (Exception)
        {
            return WriteFailure("internal_error", "发生未预期错误。");
        }

        int WriteFailure(string code, string message, string? providerCode = null)
        {
            var finalCode = invocation.CommitTerminal(code)!;
            var finalMessage = finalCode switch
            {
                "timeout" => "调用超时。",
                "cancelled" => "调用已取消。",
                _ => message
            };
            var failure = SkillResponses.Failure("grok", invocation, finalCode, finalMessage, finalCode == code ? providerCode : null);
            var canRetryRead = args[0] is "list-models" or "query" or "download" &&
                finalCode is "network_error" or "timeout" or "rate_limit_exceeded";
            failure["error"]!["retryable"] = JsonValue.Create(finalCode == "task_not_ready" || canRetryRead);
            return writer.Write(failure, ProtocolWriter.ExitCode(finalCode));
        }

        int StatusExit(JsonObject response, ProtocolWriter protocol)
        {
            var status = response["status"]?.GetValue<string>();
            if (status == "failed")
            {
                var code = response["error"]?["code"]?.GetValue<string>() ?? "provider_error";
                return protocol.Write(response, ProtocolWriter.ExitCode(code));
            }

            return protocol.Write(response);
        }
    }

    private async Task<JsonObject> GenerateAsync(string[] args, SkillInvocation invocation, WorkspacePaths paths, VideoArtifactStore artifacts)
    {
        invocation.SetTimeout(300);
        var options = await new VideoGenerationOptionsFactory(paths).CreateAsync(args[1..], invocation);
        invocation.ThrowIfCancellationRequested();
        var request = GrokVideoClient.Map(options);
        if (options.TryRun)
        {
            return VideoTryRunResponse.Build(options, invocation, GrokVideoClient.Preview(request));
        }

        if (!options.NoWait)
        {
            VideoValidator.EnsureTools();
        }

        var provider = new GrokVideoClient(client, pollInterval);
        var submitted = await provider.SubmitAsync(options, request, invocation);
        if (options.NoWait)
        {
            return submitted;
        }

        var taskId = submitted["task_id"]!.GetValue<string>();
        var job = await provider.WaitAsync(options.Endpoint, taskId, options.ApiKey, invocation);
        if (job.Status != "done")
        {
            throw new SkillException(GrokVideoErrors.Normalize(job.ErrorCode), "Grok 视频生成失败。", job.ErrorCode);
        }

        invocation.TaskId = taskId;
        invocation.RemoteCancellation = "unsupported";
        var videos = await provider.DownloadAsync(job, options.OutputDirectory!, invocation, artifacts);
        return SkillResponses.Generated("grok", "video", invocation, videos);
    }

    private async Task<JsonObject> ListAsync(string[] args, SkillInvocation invocation)
    {
        var options = ModelListOptions.Parse(args[1..], GrokVideoMetadata.DefaultApiUrl, invocation, defaultTimeout: 300);
        if (string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Any(char.IsControl))
        {
            throw new SkillException("invalid_parameter", "api-key 必须非空且不含控制字符。");
        }
        return await new GrokVideoClient(client, pollInterval).ListModelsAsync(options, invocation);
    }

    private async Task<JsonObject> QueryAsync(string[] args, SkillInvocation invocation, WorkspacePaths paths)
    {
        var options = await TaskOperationOptionsFactory.CreateAsync(args[1..], false, paths, invocation);
        var provider = new GrokVideoClient(client, pollInterval);
        var job = options.Wait
            ? await provider.WaitAsync(options.Endpoint, options.TaskId, options.ApiKey, invocation)
            : await provider.QueryOnceAsync(options, invocation);
        return GrokVideoClient.TaskView(job, invocation);
    }

    private async Task<JsonObject> DownloadAsync(string[] args, SkillInvocation invocation, WorkspacePaths paths, VideoArtifactStore artifacts)
    {
        var options = await TaskOperationOptionsFactory.CreateAsync(args[1..], true, paths, invocation);
        var provider = new GrokVideoClient(client, pollInterval);
        var job = await provider.QueryOnceAsync(options, invocation);
        if (job.Status == "pending")
        {
            throw new SkillException("task_not_ready", "视频尚未完成。");
        }

        if (job.Status != "done")
        {
            throw new SkillException(GrokVideoErrors.Normalize(job.ErrorCode), "Grok 视频生成失败。", job.ErrorCode);
        }

        invocation.TaskId = job.TaskId;
        invocation.RemoteStatus = job.Status;
        invocation.RemoteCancellation = "unsupported";
        var videos = await provider.DownloadAsync(job, options.OutputDirectory!, invocation, artifacts);
        return SkillResponses.Generated("grok", "video", invocation, videos);
    }

    private static JsonObject Local(string[] args, string? id)
    {
        ValidateLocalCommand(args, id);
        return args[0] switch
        {
            "metadata" => GrokVideoMetadata.BuildMetadata(id),
            "version" => GrokVideoMetadata.BuildVersion(id),
            _ => throw new SkillException("invalid_parameter", "未知命令。")
        };
    }

    private static void ValidateLocalCommand(string[] args, string? id)
    {
        var allowed = new HashSet<string>(["request-id", "output-format", "verbose"], StringComparer.Ordinal);
        var switches = new HashSet<string>(["verbose"], StringComparer.Ordinal);
        var options = CommandLineParser.Parse(args[1..], allowed, switches);
        if (id is null || options.TryGetValue("output-format", out var format) && format != "json")
        {
            throw new SkillException("invalid_parameter", "必须提供 request-id，输出格式仅支持 json。");
        }
    }
}
