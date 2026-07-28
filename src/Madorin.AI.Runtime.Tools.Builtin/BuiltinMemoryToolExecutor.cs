using System.Text.Json;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed class BuiltinMemoryToolExecutor(MemoryFileService memoryFileService) : IToolExecutor
{
    private readonly MemoryFileService _memoryFileService = memoryFileService
        ?? throw new ArgumentNullException(nameof(memoryFileService));

    public bool CanExecute(string toolId) =>
        toolId is BuiltinToolRegistry.MemoryReadToolId
            or BuiltinToolRegistry.MemoryAppendToolId
            or BuiltinToolRegistry.MemoryUpdateToolId
            or BuiltinToolRegistry.MemoryDeleteToolId;

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanExecute(invocation.ToolId))
        {
            return Failure(invocation, "The memory executor cannot execute this tool.");
        }

        if (IsWriteTool(invocation.ToolId) && !HasValidApproval(invocation))
        {
            return Failure(invocation, "An explicit user approval grant is required.");
        }

        try
        {
            using var arguments = JsonDocument.Parse(invocation.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Tool arguments must be a JSON object.");
            }

            return invocation.ToolId switch
            {
                BuiltinToolRegistry.MemoryReadToolId =>
                    await ReadAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                BuiltinToolRegistry.MemoryAppendToolId =>
                    await AppendAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                BuiltinToolRegistry.MemoryUpdateToolId =>
                    await UpdateAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                BuiltinToolRegistry.MemoryDeleteToolId =>
                    await DeleteAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                _ => Failure(invocation, "The memory tool is not implemented.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or ArgumentOutOfRangeException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            return Failure(invocation, ex.Message);
        }
    }

    private async Task<ToolResult> ReadAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var scope = ReadScope(arguments);
        var snapshot = await _memoryFileService.ReadSnapshotAsync(scope, ct)
            .ConfigureAwait(false);
        var lines = MemoryFileService.SplitLines(snapshot?.Content);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("scope", scope.ToString().ToLowerInvariant());
            writer.WriteNumber("lineCount", lines.Count);
            writer.WriteStartArray("lines");
            for (var index = 0; index < lines.Count; index++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", index + 1);
                writer.WriteString("text", lines[index]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (snapshot is null)
            {
                writer.WriteNull("hash");
            }
            else
            {
                writer.WriteString("hash", snapshot.Hash);
            }

            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private async Task<ToolResult> AppendAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var scope = ReadScope(arguments);
        var lines = GetLines(arguments);
        var expectedHash = GetNullableString(arguments, "expectedHash");
        var snapshot = await _memoryFileService.AppendLinesAsync(
                scope,
                lines,
                expectedHash,
                ct)
            .ConfigureAwait(false);
        return MutationResult(invocation, "appended", snapshot);
    }

    private async Task<ToolResult> UpdateAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var scope = ReadScope(arguments);
        var startLine = GetRequiredInt(arguments, "startLine");
        var endLine = GetRequiredInt(arguments, "endLine");
        var lines = GetLines(arguments);
        var expectedHash = GetRequiredString(arguments, "expectedHash");
        var snapshot = await _memoryFileService.UpdateLinesAsync(
                scope,
                startLine,
                endLine,
                lines,
                expectedHash,
                ct)
            .ConfigureAwait(false);
        return MutationResult(invocation, "updated", snapshot);
    }

    private async Task<ToolResult> DeleteAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var scope = ReadScope(arguments);
        var startLine = GetRequiredInt(arguments, "startLine");
        var endLine = GetRequiredInt(arguments, "endLine");
        var expectedHash = GetRequiredString(arguments, "expectedHash");
        var snapshot = await _memoryFileService.DeleteLinesAsync(
                scope,
                startLine,
                endLine,
                expectedHash,
                ct)
            .ConfigureAwait(false);
        return MutationResult(invocation, "deleted", snapshot);
    }

    private static ToolResult MutationResult(
        ToolInvocation invocation,
        string action,
        MemoryFileSnapshot snapshot)
    {
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean(action, true);
            writer.WriteNumber("lineCount", MemoryFileService.SplitLines(snapshot.Content).Count);
            writer.WriteString("hash", snapshot.Hash);
            writer.WriteString("appliesFrom", "nextInvocation");
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    internal static MemoryScope ReadScope(JsonElement arguments)
    {
        var value = GetRequiredString(arguments, "scope");
        if (!Enum.TryParse<MemoryScope>(value, ignoreCase: true, out var scope)
            || scope is MemoryScope.Effective)
        {
            throw new ArgumentException($"Memory scope '{value}' is not supported.");
        }

        return scope;
    }

    internal static string GetRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static string? GetNullableString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' is required.");
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string or null.");
        }

        return property.GetString();
    }

    private static int GetRequiredInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be an integer.");
        }

        return value;
    }

    private static List<string> GetLines(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("lines", out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Tool argument 'lines' must be an array.");
        }

        var lines = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new ArgumentException("Each memory line must be a non-empty string.");
            }

            var line = item.GetString()!;
            if (line.Contains('\r') || line.Contains('\n'))
            {
                throw new ArgumentException("Each memory line must contain no newline.");
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException("Tool argument 'lines' must not be empty.");
        }

        return lines;
    }

    private static bool IsWriteTool(string toolId) =>
        toolId is BuiltinToolRegistry.MemoryAppendToolId
            or BuiltinToolRegistry.MemoryUpdateToolId
            or BuiltinToolRegistry.MemoryDeleteToolId;

    private static bool HasValidApproval(ToolInvocation invocation) =>
        invocation.PermissionContext is { } permission
        && string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal)
        && (permission.ExpiresAt is null || permission.ExpiresAt > DateTimeOffset.UtcNow);

    private static ToolResult Failure(ToolInvocation invocation, string error) =>
        new(invocation.CallId, invocation.ToolId, false, Error: error);
}
