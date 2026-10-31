using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Executes a multi-command script through the host platform's shell.</summary>
public sealed class BuiltinCommandToolExecutor : IToolExecutor
{
    public bool CanExecute(string toolId) => string.Equals(
        toolId,
        BuiltinToolRegistry.CommandToolId,
        StringComparison.Ordinal);

    public ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The command executor cannot execute this tool.");
        }

        var permission = invocation.PermissionContext
            ?? throw new UnauthorizedAccessException("A tool permission grant is required.");
        if (!string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The tool permission grant belongs to another Run.");
        }

        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        var arguments = document.RootElement;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool arguments must be a JSON object.");
        }

        var script = GetRequiredString(arguments, "script");
        var encoding = BuiltinCommandRunner.ResolveEncoding(GetOptionalString(arguments, "encoding"));
        var shell = BuiltinCommandRunner.NormalizeShell(GetOptionalString(arguments, "shell"));
        var executable = BuiltinCommandRunner.ResolveShell(shell, permission.AllowedExecutables);
        try
        {
            var policy = new BuiltinFsPathPolicy(permission);
            var workingDirectory = policy.PinReadDirectory(
                GetOptionalString(arguments, "workingDirectory") ?? permission.WorkspaceRoot);
            var environment = BuiltinProcessRunner.BuildEnvironment(
                arguments,
                permission.AllowedEnvironmentVariables,
                inheritAll: true);
            var standardInput = BuiltinProcessRunner.ParseStandardInput(arguments);
            var effectiveShell = ResolveEffectiveShell(executable.Path, shell);
            var processArguments = BuiltinCommandRunner.BuildArguments(effectiveShell, script, encoding);
            return ValueTask.FromResult<IPreparedToolExecution>(
                new PreparedCommandExecution(
                    invocation,
                    executable,
                    workingDirectory,
                    processArguments,
                    environment,
                    standardInput,
                    encoding,
                    $"shell:{Path.GetFileName(executable.Path)}"));
        }
        catch
        {
            executable.Dispose();
            throw;
        }
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        try
        {
            await using var prepared = await PrepareAsync(invocation, ct).ConfigureAwait(false);
            return await prepared.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return new ToolResult(invocation.CallId, invocation.ToolId, false, Error: ex.Message);
        }
    }

    internal static string GetRequiredString(JsonElement arguments, string propertyName) =>
        GetOptionalString(arguments, propertyName)
        ?? throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");

    internal static string? GetOptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    private static string ResolveEffectiveShell(string path, string requested)
    {
        if (requested != "auto")
        {
            return requested;
        }

        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name is "pwsh" or "powershell" or "cmd" or "bash" or "zsh" or "sh"
            ? name
            : throw new InvalidOperationException("The resolved shell is not supported.");
    }

    internal sealed class PreparedCommandExecution(
        ToolInvocation invocation,
        BuiltinProcessRunner.PinnedExecutable executable,
        PinnedPath workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? standardInput,
        Encoding encoding,
        string targetSummary) : IPreparedToolExecution
    {
        private int _disposed;
        public string TargetSummary { get; } = targetSummary;

        public async Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                var output = await BuiltinProcessRunner.RunAsync(
                    executable.Path,
                    arguments,
                    workingDirectory.Path,
                    environment,
                    standardInput,
                    encoding,
                    ct).ConfigureAwait(false);
                var json = ToolJson.Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("exitCode", output.ExitCode);
                    writer.WriteString("stdout", output.StandardOutput);
                    writer.WriteString("stderr", output.StandardError);
                    writer.WriteBoolean("stdoutTruncated", output.StandardOutputTruncated);
                    writer.WriteBoolean("stderrTruncated", output.StandardErrorTruncated);
                    writer.WriteString("shell", Path.GetFileName(executable.Path));
                    writer.WriteEndObject();
                });
                return new ToolResult(invocation.CallId, invocation.ToolId, true, json);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                return new ToolResult(invocation.CallId, invocation.ToolId, false, Error: ex.Message);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                workingDirectory.Dispose();
                executable.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
