using System.Buffers;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>Adapts the file Skill runner to the Runtime Tool Gateway contract.</summary>
public sealed class AgentFileSkillScriptToolExecutor : IToolExecutor
{
    private readonly AgentFileSkillScriptRunner? _runner;
    private readonly Func<string, SkillCatalogSnapshot?>? _snapshotResolver;
    private readonly SkillCatalogSnapshot? _snapshot;
    private readonly string? _workspaceRoot;
    private readonly string? _runId;

    public AgentFileSkillScriptToolExecutor(
        AgentFileSkillScriptRunner runner,
        SkillCatalogSnapshot snapshot)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public AgentFileSkillScriptToolExecutor(
        string workspaceRoot,
        string runId,
        Func<string, SkillCatalogSnapshot?> snapshotResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _runId = runId;
        _snapshotResolver = snapshotResolver
            ?? throw new ArgumentNullException(nameof(snapshotResolver));
    }

    public AgentFileSkillScriptToolExecutor(
        AgentFileSkillScriptRunner runner,
        SkillCatalogService catalog)
        : this(
            runner,
            (catalog ?? throw new ArgumentNullException(nameof(catalog))).Current)
    {
    }

    public bool CanExecute(string toolId) => string.Equals(toolId, SkillScriptAuthorization.ToolId, StringComparison.Ordinal);

    public async ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        var request = ParseRequest(invocation);
        var snapshot = ResolveSnapshot(request.CatalogVersion);
        await ValidateSnapshotAsync(request, snapshot, ct).ConfigureAwait(false);
        var runner = _runner
            ?? new AgentFileSkillScriptRunner(
                snapshot,
                _workspaceRoot!,
                _runId!);
        return new Prepared(runner, invocation, request);
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        await using var prepared = await PrepareAsync(invocation, ct).ConfigureAwait(false);
        return await prepared.ExecuteAsync(ct).ConfigureAwait(false);
    }

    private static SkillScriptRequest ParseRequest(ToolInvocation invocation)
    {
        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        var root = document.RootElement;
        var arguments = root.TryGetProperty("arguments", out var argumentElement)
            ? argumentElement.EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray()
            : Array.Empty<string>();
        return new SkillScriptRequest(
            root.GetProperty("skillName").GetString() ?? string.Empty,
            root.GetProperty("script").GetString() ?? string.Empty,
            arguments,
            root.GetProperty("catalogVersion").GetString(),
            invocation.RunId,
            root.TryGetProperty("timeoutMilliseconds", out var timeout)
                ? TimeSpan.FromMilliseconds(timeout.GetInt32())
                : null,
            invocation.PermissionContext?.AllowedEnvironmentVariables?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            root.GetProperty("scriptHash").GetString(),
            root.GetProperty("interpreter").GetString());

    }

    private SkillCatalogSnapshot ResolveSnapshot(string? catalogVersion)
    {
        if (string.IsNullOrWhiteSpace(catalogVersion))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillRefreshFailed,
                "The Skill script request does not identify a Catalog version.");
        }

        var snapshot = _snapshot ?? _snapshotResolver?.Invoke(catalogVersion);
        return snapshot
            ?? throw new SkillScriptException(
                RuntimeErrorCodes.SkillRefreshFailed,
                $"Skill catalog '{catalogVersion}' was not captured by this Run.");
    }

    private static async Task ValidateSnapshotAsync(
        SkillScriptRequest request,
        SkillCatalogSnapshot snapshot,
        CancellationToken ct)
    {
        if (!string.Equals(request.CatalogVersion, snapshot.Version, StringComparison.Ordinal))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillRefreshFailed,
                "The Skill catalog changed before script execution.");
        }

        if (!snapshot.TryGetSkill(request.SkillName, out var skill) || skill is null)
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptUnsupported, "The Skill is not in the fixed catalog.");
        }

        if (!skill.Scripts.Contains(request.Script, StringComparer.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptUnsupported, "The script is not declared by the Skill.");
        }

        var path = Path.Combine(skill.MaterializedPath, request.Script.Replace('/', Path.DirectorySeparatorChar));
        var actualHash = SkillScriptAuthorization.ComputeScriptHash(path);
        if (!string.Equals(request.ScriptHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillRefreshFailed, "The script content hash does not match the catalog snapshot.");
        }

        var interpreter = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(
            Path.GetExtension(path),
            ct).ConfigureAwait(false);
        if (!string.Equals(request.Interpreter, interpreter, StringComparison.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptInterpreterUnavailable, "The authorized interpreter does not match the script extension.");
        }
    }

    private sealed class Prepared(
        AgentFileSkillScriptRunner runner,
        ToolInvocation invocation,
        SkillScriptRequest request) : IPreparedToolExecution
    {
        public string TargetSummary => $"skill:{request.SkillName}/{request.Script}";

        public async Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await runner.RunAsync(request, ct).ConfigureAwait(false);
                var output = SerializeResult(result);
                return new ToolResult(invocation.CallId, invocation.ToolId, result.ExitCode == 0, output,
                    result.ExitCode == 0 ? null : $"Skill script exited with code {result.ExitCode}.");
            }
            catch (SkillScriptException ex)
            {
                return new ToolResult(invocation.CallId, invocation.ToolId, false, "{}", ex.Message);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string SerializeResult(SkillScriptExecutionResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("skillName", result.SkillName);
            writer.WriteString("script", result.Script);
            writer.WriteString("interpreter", result.Interpreter);
            writer.WriteString("catalogVersion", result.CatalogVersion);
            writer.WriteNumber("exitCode", result.ExitCode);
            writer.WriteString("stdout", result.StandardOutput);
            writer.WriteString("stderr", result.StandardError);
            writer.WriteNumber("durationMilliseconds", result.Duration.TotalMilliseconds);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
