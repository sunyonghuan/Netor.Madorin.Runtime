using System.Text.Json;
using Madorin.AI.Runtime.Services.AgentJobs;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>
/// 并行子 Agent 作业工具：create / create_many / status / result / wait / cancel。
/// 不含定时；结果通过 result/wait 回收。
/// </summary>
public sealed class BuiltinAgentJobToolExecutor(IAgentJobCoordinator coordinator) : IToolExecutor
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        BuiltinToolRegistry.AgentJobCreateToolId,
        BuiltinToolRegistry.AgentJobCreateManyToolId,
        BuiltinToolRegistry.AgentJobStatusToolId,
        BuiltinToolRegistry.AgentJobResultToolId,
        BuiltinToolRegistry.AgentJobWaitToolId,
        BuiltinToolRegistry.AgentJobCancelToolId
    };

    private readonly IAgentJobCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public bool CanExecute(string toolId) => Supported.Contains(toolId);

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The agent job executor cannot execute this tool.");
        }

        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(invocation, "Tool arguments must be a JSON object.");
            }

            return invocation.ToolId switch
            {
                BuiltinToolRegistry.AgentJobCreateToolId =>
                    CreateOne(invocation, root),
                BuiltinToolRegistry.AgentJobCreateManyToolId =>
                    CreateMany(invocation, root),
                BuiltinToolRegistry.AgentJobStatusToolId =>
                    Status(invocation, root),
                BuiltinToolRegistry.AgentJobResultToolId =>
                    Result(invocation, root),
                BuiltinToolRegistry.AgentJobWaitToolId =>
                    await WaitAsync(invocation, root, ct).ConfigureAwait(false),
                BuiltinToolRegistry.AgentJobCancelToolId =>
                    Cancel(invocation, root),
                _ => Error(invocation, $"Unsupported tool id '{invocation.ToolId}'.")
            };
        }
        catch (JsonException ex)
        {
            return Error(invocation, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(invocation, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(invocation, ex.Message);
        }
    }

    private ToolResult CreateOne(ToolInvocation invocation, JsonElement root)
    {
        if (!TryReadCreateRequest(root, out var request, out var error))
        {
            return Error(invocation, error!);
        }

        var snapshot = _coordinator.Create(
            request,
            invocation.RunId,
            invocation.SessionId,
            invocation.AgentId);
        var json = ToolJson.Write(writer => WriteSnapshot(writer, snapshot, includeResult: false));
        return Ok(invocation, json);
    }

    private ToolResult CreateMany(ToolInvocation invocation, JsonElement root)
    {
        if (!root.TryGetProperty("jobs", out var jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Array
            || jobsElement.GetArrayLength() == 0)
        {
            return Error(invocation, "Missing or empty 'jobs' array.");
        }

        var requests = new List<AgentJobCreateRequest>();
        foreach (var item in jobsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return Error(invocation, "Each jobs[] item must be an object with agentName and task.");
            }

            if (!TryReadCreateRequest(item, out var request, out var error))
            {
                return Error(invocation, error ?? "Each jobs[] item must be an object with agentName and task.");
            }

            requests.Add(request);
        }

        var snapshots = _coordinator.CreateMany(
            requests,
            invocation.RunId,
            invocation.SessionId,
            invocation.AgentId);
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteStartArray("jobs");
            foreach (var snapshot in snapshots)
            {
                WriteSnapshotObject(writer, snapshot, includeResult: false);
            }

            writer.WriteEndArray();
            writer.WriteString("message", $"Created {snapshots.Count} agent job(s).");
            writer.WriteEndObject();
        });
        return Ok(invocation, json);
    }

    private ToolResult Status(ToolInvocation invocation, JsonElement root)
    {
        if (!TryReadJobId(root, out var jobId, out var error))
        {
            return Error(invocation, error!);
        }

        var snapshot = _coordinator.GetSnapshot(jobId);
        if (snapshot is null)
        {
            return Error(invocation, $"Agent job '{jobId}' was not found.");
        }

        var json = ToolJson.Write(writer => WriteSnapshot(writer, snapshot, includeResult: false));
        return Ok(invocation, json);
    }

    private ToolResult Result(ToolInvocation invocation, JsonElement root)
    {
        // 批量回收：jobIds
        if (root.TryGetProperty("jobIds", out var jobIdsElement)
            && jobIdsElement.ValueKind == JsonValueKind.Array)
        {
            var jobIds = new List<string>();
            foreach (var item in jobIdsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    jobIds.Add(item.GetString()!.Trim());
                }
            }

            if (jobIds.Count == 0)
            {
                return Error(invocation, "jobIds must be a non-empty string array.");
            }

            var collects = _coordinator.CollectMany(jobIds);
            var allReady = collects.All(static c => c.Ready);
            var allSuccess = collects.All(static c => c.Success);
            var toolSuccess = allReady && allSuccess;
            var json = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", toolSuccess);
                writer.WriteBoolean("allReady", allReady);
                writer.WriteBoolean("allSucceeded", allSuccess);
                writer.WriteStartArray("jobs");
                foreach (var item in collects)
                {
                    WriteCollectObject(writer, item);
                }

                writer.WriteEndArray();
                writer.WriteString(
                    "message",
                    toolSuccess
                        ? $"Recovered {collects.Count} successful agent job(s)."
                        : allReady
                            ? "All listed jobs are terminal, but one or more did not succeed."
                            : "One or more agent jobs are not ready yet.");
                writer.WriteEndObject();
            });
            return new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                toolSuccess,
                json,
                toolSuccess ? null : "Not all agent jobs succeeded.");
        }

        if (!TryReadJobId(root, out var jobId, out var error))
        {
            return Error(invocation, error!);
        }

        var collected = _coordinator.Collect(jobId);
        var singleJson = ToolJson.Write(writer => WriteCollect(writer, collected));
        return new ToolResult(
            invocation.CallId,
            invocation.ToolId,
            collected.Ready && collected.Success,
            singleJson,
            collected.Ready && collected.Success ? null : collected.Message);
    }

    private async Task<ToolResult> WaitAsync(
        ToolInvocation invocation,
        JsonElement root,
        CancellationToken ct)
    {
        var jobIds = new List<string>();
        if (root.TryGetProperty("jobIds", out var jobIdsElement)
            && jobIdsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jobIdsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    jobIds.Add(item.GetString()!.Trim());
                }
            }
        }
        else if (root.TryGetProperty("jobId", out var jobIdElement)
            && jobIdElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(jobIdElement.GetString()))
        {
            jobIds.Add(jobIdElement.GetString()!.Trim());
        }

        if (jobIds.Count == 0)
        {
            return Error(invocation, "Provide jobId or a non-empty jobIds array.");
        }

        var timeoutMs = 300_000;
        if (root.TryGetProperty("timeoutMs", out var timeoutElement)
            && timeoutElement.ValueKind == JsonValueKind.Number
            && timeoutElement.TryGetInt32(out var parsedTimeout))
        {
            timeoutMs = parsedTimeout;
        }

        AgentJobWaitResult wait;
        try
        {
            wait = await _coordinator.WaitAsync(jobIds, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Error(invocation, ex.Message);
        }

        var collects = _coordinator.CollectMany(jobIds);
        var toolSuccess = wait.AllTerminal && wait.AllSucceeded;
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", toolSuccess);
            writer.WriteBoolean("allTerminal", wait.AllTerminal);
            writer.WriteBoolean("allSucceeded", wait.AllSucceeded);
            writer.WriteBoolean("timedOut", wait.TimedOut);
            writer.WriteStartArray("jobs");
            foreach (var item in collects)
            {
                WriteCollectObject(writer, item, wait.Jobs.FirstOrDefault(job => job.JobId == item.JobId));
            }

            writer.WriteEndArray();
            writer.WriteString(
                "message",
                toolSuccess
                    ? $"Recovered {collects.Count} successful agent job(s)."
                    : wait.TimedOut
                        ? "Wait timed out before all agent jobs finished."
                        : wait.AllTerminal
                            ? "All agent jobs finished, but one or more did not succeed."
                            : "Wait ended before all agent jobs finished.");
            writer.WriteEndObject();
        });

        return new ToolResult(
            invocation.CallId,
            invocation.ToolId,
            toolSuccess,
            json,
            toolSuccess ? null : "Not all agent jobs succeeded.");
    }

    private ToolResult Cancel(ToolInvocation invocation, JsonElement root)
    {
        if (!TryReadJobId(root, out var jobId, out var error))
        {
            return Error(invocation, error!);
        }

        var snapshotBefore = _coordinator.GetSnapshot(jobId);
        if (snapshotBefore is null)
        {
            return Error(invocation, $"Agent job '{jobId}' was not found.");
        }

        if (AgentJobStates.IsTerminal(snapshotBefore.State))
        {
            var alreadyJson = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", false);
                writer.WriteBoolean("cancelRequested", false);
                writer.WriteString("jobId", jobId);
                writer.WriteString("state", snapshotBefore.State);
                writer.WriteString(
                    "message",
                    $"Agent job '{jobId}' was already terminal (state={snapshotBefore.State}).");
                writer.WriteEndObject();
            });
            return new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                alreadyJson,
                $"Agent job '{jobId}' was already terminal.");
        }

        var requested = _coordinator.TryCancel(jobId);
        var snapshot = _coordinator.GetSnapshot(jobId);
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", requested);
            writer.WriteBoolean("cancelRequested", requested);
            writer.WriteString("jobId", jobId);
            if (snapshot is not null)
            {
                writer.WriteString("state", snapshot.State);
            }

            writer.WriteString(
                "message",
                requested
                    ? $"Agent job '{jobId}' cancel requested (state={snapshot?.State ?? AgentJobStates.Cancelling})."
                    : $"Failed to request cancel for agent job '{jobId}'.");
            writer.WriteEndObject();
        });
        return new ToolResult(
            invocation.CallId,
            invocation.ToolId,
            requested,
            json,
            requested ? null : $"Failed to request cancel for agent job '{jobId}'.");
    }

    private static bool TryReadCreateRequest(
        JsonElement root,
        out AgentJobCreateRequest request,
        out string? error)
    {
        request = null!;
        error = null;
        if (!root.TryGetProperty("agentName", out var agentNameElement)
            || agentNameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentNameElement.GetString()))
        {
            error = "Missing or invalid 'agentName' parameter.";
            return false;
        }

        if (!root.TryGetProperty("task", out var taskElement)
            || taskElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(taskElement.GetString()))
        {
            error = "Missing or invalid 'task' parameter.";
            return false;
        }

        int? timeoutMs = null;
        if (root.TryGetProperty("timeoutMs", out var timeoutElement)
            && timeoutElement.ValueKind == JsonValueKind.Number
            && timeoutElement.TryGetInt32(out var parsed))
        {
            timeoutMs = parsed;
        }

        var createIfMissing = root.TryGetProperty("createIfMissing", out var createElement)
            && createElement.ValueKind == JsonValueKind.True;
        var temporary = root.TryGetProperty("temporary", out var temporaryElement)
            && temporaryElement.ValueKind == JsonValueKind.True;
        var role = root.TryGetProperty("role", out var roleElement)
            && roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString()
            : null;
        var capabilities = ReadStringArray(root, "capabilities");
        var allowedToolIds = ReadStringArray(root, "allowedToolIds");
        request = new AgentJobCreateRequest(
            agentNameElement.GetString()!.Trim(),
            taskElement.GetString()!.Trim(),
            timeoutMs,
            createIfMissing,
            temporary,
            role,
            capabilities,
            allowedToolIds);
        return true;
    }

    private static string[]? ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static bool TryReadJobId(JsonElement root, out string jobId, out string? error)
    {
        jobId = string.Empty;
        error = null;
        if (!root.TryGetProperty("jobId", out var jobIdElement)
            || jobIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(jobIdElement.GetString()))
        {
            error = "Missing or invalid 'jobId' parameter.";
            return false;
        }

        jobId = jobIdElement.GetString()!.Trim();
        return true;
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, AgentJobSnapshot snapshot, bool includeResult)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("success", true);
        WriteSnapshotFields(writer, snapshot, includeResult);
        writer.WriteString("message", $"Agent job '{snapshot.JobId}' state={snapshot.State}.");
        writer.WriteEndObject();
    }

    private static void WriteSnapshotObject(Utf8JsonWriter writer, AgentJobSnapshot snapshot, bool includeResult)
    {
        writer.WriteStartObject();
        WriteSnapshotFields(writer, snapshot, includeResult);
        writer.WriteEndObject();
    }

    private static void WriteSnapshotFields(Utf8JsonWriter writer, AgentJobSnapshot snapshot, bool includeResult)
    {
        writer.WriteString("jobId", snapshot.JobId);
        writer.WriteString("state", snapshot.State);
        writer.WriteString("agentName", snapshot.AgentName);
        writer.WriteString("task", snapshot.Task);
        if (snapshot.StartedAt is { } started)
        {
            writer.WriteString("startedAt", started);
        }
        if (snapshot.CompletedAt is { } completed)
        {
            writer.WriteString("completedAt", completed);
        }
        writer.WriteBoolean("terminal", AgentJobStates.IsTerminal(snapshot.State));
        if (includeResult && snapshot.Result is not null)
        {
            writer.WriteString("result", snapshot.Result);
        }

        if (snapshot.Error is not null)
        {
            writer.WriteString("error", snapshot.Error);
        }
    }

    private static void WriteCollect(Utf8JsonWriter writer, AgentJobCollectResult collected)
    {
        writer.WriteStartObject();
        WriteCollectFields(writer, collected);
        writer.WriteEndObject();
    }

    private static void WriteCollectObject(Utf8JsonWriter writer, AgentJobCollectResult collected,
        AgentJobSnapshot? snapshot = null)
    {
        writer.WriteStartObject();
        WriteCollectFields(writer, collected);
        if (snapshot is not null)
        {
            if (snapshot.StartedAt is { } started)
            {
                writer.WriteString("startedAt", started);
            }
            if (snapshot.CompletedAt is { } completed)
            {
                writer.WriteString("completedAt", completed);
            }
            writer.WriteStartArray("childAgentIds");
            foreach (var id in (snapshot.Evidence ?? []).Select(static item => item.AgentId).Distinct())
            {
                writer.WriteStringValue(id);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteCollectFields(Utf8JsonWriter writer, AgentJobCollectResult collected)
    {
        writer.WriteBoolean("success", collected.Success);
        writer.WriteBoolean("ready", collected.Ready);
        writer.WriteString("jobId", collected.JobId);
        writer.WriteString("state", collected.State);
        writer.WriteString("agentName", collected.AgentName);
        writer.WriteString("task", collected.Task);
        if (collected.Result is not null)
        {
            writer.WriteString("result", collected.Result);
        }

        if (collected.Error is not null)
        {
            writer.WriteString("error", collected.Error);
        }

        writer.WriteString("message", collected.Message);
    }

    private static ToolResult Ok(ToolInvocation invocation, string outputJson) =>
        new(invocation.CallId, invocation.ToolId, true, outputJson);

    private static ToolResult Error(ToolInvocation invocation, string message)
    {
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, false, json, message);
    }
}
