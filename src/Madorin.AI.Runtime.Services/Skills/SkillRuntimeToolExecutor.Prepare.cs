using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Skills;

public sealed partial class SkillRuntimeToolExecutor
{
    private async Task<Immediate> PrepareUnifiedReadAsync(
        ToolInvocation invocation,
        SkillCatalogSnapshot snapshot,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        var resource = document.RootElement.TryGetProperty("resource", out var resourceElement)
            ? resourceElement.GetString()
            : null;
        return string.IsNullOrWhiteSpace(resource)
            ? PrepareLoad(invocation, snapshot)
            : await PrepareReadAsync(invocation, snapshot, ct).ConfigureAwait(false);
    }

    private Immediate PrepareLoad(ToolInvocation invocation, SkillCatalogSnapshot snapshot)
    {
        var name = ReadRequired(invocation.ArgumentsJson, "name");
        var skill = RequireSkill(invocation, snapshot, name);
        var path = Path.Combine(skill.MaterializedPath, "SKILL.md");
        var content = File.ReadAllText(path);
        return new Immediate(invocation, WriteObject(("name", name), ("content", content)));
    }

    private async Task<Immediate> PrepareReadAsync(
        ToolInvocation invocation,
        SkillCatalogSnapshot snapshot,
        CancellationToken ct)
    {
        var name = ReadRequired(invocation.ArgumentsJson, "name");
        var resource = ReadRequired(invocation.ArgumentsJson, "resource");
        var skill = RequireSkill(invocation, snapshot, name);
        var normalized = resource.Replace('\\', '/').TrimStart('/');
        if (normalized.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            || !skill.Resources.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillResourceBlocked,
                $"Resource '{resource}' is not declared by Skill '{name}'.");
        }

        var combined = Path.GetFullPath(
            Path.Combine(skill.MaterializedPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(skill.MaterializedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(root, StringComparison.Ordinal))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillResourceBlocked,
                $"Resource '{resource}' is outside the Skill snapshot.");
        }

        var bytes = await File.ReadAllBytesAsync(combined, ct).ConfigureAwait(false);
        return new Immediate(
            invocation,
            WriteObject(
                ("name", name),
                ("resource", resource),
                ("content", Encoding.UTF8.GetString(bytes))));
    }

    private RunPrepared PrepareRun(ToolInvocation invocation, SkillCatalogSnapshot snapshot)
    {
        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        var root = document.RootElement;
        var skillName = root.TryGetProperty("skillName", out var skillElement)
            ? skillElement.GetString() ?? string.Empty
            : ReadRequired(invocation.ArgumentsJson, "name");
        var script = root.GetProperty("script").GetString() ?? string.Empty;
        var arguments = root.TryGetProperty("arguments", out var argumentElement)
            && argumentElement.ValueKind == JsonValueKind.Array
            ? argumentElement.EnumerateArray()
                .Select(static value => value.GetString() ?? string.Empty)
                .ToArray()
            : [];
        RequireSkill(invocation, snapshot, skillName);
        return new RunPrepared(
            invocation,
            new AgentFileSkillScriptRunner(snapshot, _workspaceRoot, _runId),
            new SkillScriptRequest(skillName, script, arguments, snapshot.Version, invocation.RunId));
    }

    private sealed class RunPrepared(
        ToolInvocation invocation,
        AgentFileSkillScriptRunner runner,
        SkillScriptRequest request) : IPreparedToolExecution
    {
        public string TargetSummary => $"skill:{request.SkillName}/{request.Script}";

        public async Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await runner.RunAsync(request, ct).ConfigureAwait(false);
                var json = WriteRunResult(result);
                return new ToolResult(
                    invocation.CallId,
                    invocation.ToolId,
                    result.ExitCode == 0,
                    json,
                    result.ExitCode == 0 ? null : $"Skill script exited with code {result.ExitCode}.");
            }
            catch (SkillScriptException ex)
            {
                return new ToolResult(invocation.CallId, invocation.ToolId, false, "{}", ex.Message);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string WriteObject(params (string Name, string Value)[] properties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string WriteRunResult(SkillScriptExecutionResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("skillName", result.SkillName);
            writer.WriteString("script", result.Script);
            writer.WriteString("interpreter", result.Interpreter);
            writer.WriteNumber("exitCode", result.ExitCode);
            writer.WriteString("stdout", result.StandardOutput);
            writer.WriteString("stderr", result.StandardError);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
