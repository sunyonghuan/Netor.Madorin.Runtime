using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Resolves built-in tool arguments into a fail-closed, side-effect-free consent scope.</summary>
public sealed class BuiltinToolConsentScopeResolver : IToolConsentScopeResolver
{
    private const long DefaultMaxHttpResponseBytes = 1024 * 1024;

    public ToolConsentScopeResolution Resolve(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!string.Equals(invocation.ToolId, descriptor.ToolId, StringComparison.Ordinal)
            || descriptor.ExecutionTarget is not ToolExecutionTarget.Builtin)
        {
            return ToolConsentScopeResolution.Failure(
                "The tool is not supported by the standalone consent scope resolver.");
        }

        try
        {
            using var document = JsonDocument.Parse(invocation.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ToolConsentScopeResolution.Failure(
                    "The requested tool scope is not a JSON object.");
            }

            var scope = ResolveCore(
                invocation.ToolId,
                document.RootElement,
                Path.GetFullPath(workspaceRoot));
            return ToolConsentScopeResolution.Success(scope);
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidDataException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return ToolConsentScopeResolution.Failure(
                "The requested tool scope could not be safely normalized.");
        }
    }

    private static ToolRequestedScope ResolveCore(
        string toolId,
        JsonElement arguments,
        string workspaceRoot) => toolId switch
    {
        BuiltinToolRegistry.FileListToolId => FileReadScope(
            workspaceRoot,
            BuiltinFsToolExecutor.GetOptionalString(arguments, "path") ?? ".",
            "directory listing"),
        BuiltinToolRegistry.FileStatusToolId => FileReadScope(
            workspaceRoot,
            BuiltinFsToolExecutor.GetRequiredString(arguments, "path"),
            "path metadata"),
        BuiltinToolRegistry.FileReadToolId => FileReadScope(
            workspaceRoot,
            BuiltinFsToolExecutor.GetRequiredString(arguments, "path"),
            "file read"),
        BuiltinToolRegistry.FileSearchToolId => FileReadScope(
            workspaceRoot,
            BuiltinFsToolExecutor.GetRequiredString(arguments, "path"),
            "bounded text search"),
        BuiltinToolRegistry.FileWriteToolId => FileWriteScope(
            workspaceRoot,
            BuiltinFsToolExecutor.GetRequiredString(arguments, "path")),
        BuiltinToolRegistry.FileAppendToolId => FileModifyScope(
            workspaceRoot,
            arguments,
            "append text"),
        BuiltinToolRegistry.DirectoryCreateToolId => DirectoryCreateScope(
            workspaceRoot,
            BuiltinFsToolExecutor.GetRequiredString(arguments, "path")),
        BuiltinToolRegistry.FileCopyToolId => FileCopyScope(workspaceRoot, arguments),
        BuiltinToolRegistry.FileMoveToolId => FileMoveScope(workspaceRoot, arguments),
        BuiltinToolRegistry.FileDeleteToolId => FileDeleteScope(workspaceRoot, arguments),
        BuiltinToolRegistry.FileEditToolId => FileModifyScope(
            workspaceRoot,
            arguments,
            "edit lines"),
        BuiltinToolRegistry.MemoryReadToolId => MemoryScope(arguments, isAppend: false),
        BuiltinToolRegistry.MemoryAppendToolId => MemoryScope(arguments, isAppend: true),
        BuiltinToolRegistry.ProcessRunToolId => ProcessScope(workspaceRoot, arguments),
        BuiltinToolRegistry.HttpRequestToolId => HttpScope(arguments),
        SkillScriptAuthorization.ToolId => ScriptScope(arguments, workspaceRoot),
        _ => throw new NotSupportedException("The built-in tool scope is unknown.")
    };

    private static ToolRequestedScope ScriptScope(JsonElement arguments, string workspaceRoot)
    {
        var skill = arguments.GetProperty("skillName").GetString();
        var script = arguments.GetProperty("script").GetString();
        var interpreter = arguments.GetProperty("interpreter").GetString();
        var hash = arguments.GetProperty("scriptHash").GetString();
        if (string.IsNullOrWhiteSpace(skill)
            || string.IsNullOrWhiteSpace(script)
            || string.IsNullOrWhiteSpace(interpreter)
            || string.IsNullOrWhiteSpace(hash))
        {
            throw new InvalidDataException("A Skill script scope requires skill, script, hash, and interpreter.");
        }

        return Create(
            $"skill:{skill}/{script}",
            $"script hash {hash}; interpreter {interpreter}; Runtime user permissions apply",
            readRoots: [],
            writeRoots: [],
            allowedExecutables: [interpreter],
            allowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray(),
            canGrantForRun: true);
    }

    private static ToolRequestedScope FileReadScope(
        string workspaceRoot,
        string requestedPath,
        string detail)
    {
        var path = BuiltinFsPathPolicy.ResolveRequestedPath(workspaceRoot, requestedPath);
        return Create(
            BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, path),
            detail,
            readRoots: [path],
            canGrantForRun: true);
    }

    private static ToolRequestedScope FileWriteScope(string workspaceRoot, string requestedPath)
    {
        var path = BuiltinFsPathPolicy.ResolveRequestedPath(workspaceRoot, requestedPath);
        var overwrite = File.Exists(path) || Directory.Exists(path);
        return Create(
            BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, path),
            overwrite ? "overwrite existing path" : "create file",
            writeRoots: [path],
            allowOverwrite: overwrite,
            canGrantForRun: !overwrite);
    }

    private static ToolRequestedScope DirectoryCreateScope(
        string workspaceRoot,
        string requestedPath)
    {
        var path = BuiltinFsPathPolicy.ResolveRequestedPath(workspaceRoot, requestedPath);
        return Create(
            BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, path),
            "create directory",
            writeRoots: [path],
            canGrantForRun: !Directory.Exists(path) && !File.Exists(path));
    }

    private static ToolRequestedScope FileCopyScope(string workspaceRoot, JsonElement arguments)
    {
        var source = ResolvePath(workspaceRoot, arguments, "source");
        var destination = ResolvePath(workspaceRoot, arguments, "destination");
        var overwrite = File.Exists(destination) || Directory.Exists(destination);
        return Create(
            JoinTargets(workspaceRoot, source, destination),
            overwrite ? "copy and overwrite destination" : "copy file",
            readRoots: [source],
            writeRoots: [destination],
            allowOverwrite: overwrite,
            canGrantForRun: !overwrite);
    }

    private static ToolRequestedScope FileMoveScope(string workspaceRoot, JsonElement arguments)
    {
        var source = ResolvePath(workspaceRoot, arguments, "source");
        var destination = ResolvePath(workspaceRoot, arguments, "destination");
        return Create(
            JoinTargets(workspaceRoot, source, destination),
            "move path",
            writeRoots: [source, destination],
            allowOverwrite: File.Exists(destination) || Directory.Exists(destination),
            allowMove: true);
    }

    private static ToolRequestedScope FileDeleteScope(string workspaceRoot, JsonElement arguments)
    {
        var path = ResolvePath(workspaceRoot, arguments, "path");
        return Create(
            BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, path),
            "delete path",
            writeRoots: [path],
            allowDelete: true,
            canGrantForRun: true);
    }

    private static ToolRequestedScope FileModifyScope(
        string workspaceRoot,
        JsonElement arguments,
        string detail)
    {
        var path = ResolvePath(workspaceRoot, arguments, "path");
        return Create(
            BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, path),
            detail,
            readRoots: [path],
            writeRoots: [path],
            allowOverwrite: true);
    }

    private static ToolRequestedScope MemoryScope(JsonElement arguments, bool isAppend)
    {
        var scope = BuiltinMemoryToolExecutor.ReadScope(arguments, allowEffective: !isAppend);
        var detail = isAppend
            ? $"append {BuiltinMemoryToolExecutor.GetRequiredString(arguments, "item").Length} characters"
            : "read memory snapshot";
        return Create(
            $"memory:{scope.ToString().ToLowerInvariant()}",
            detail,
            canGrantForRun: !isAppend);
    }

    private static ToolRequestedScope ProcessScope(
        string workspaceRoot,
        JsonElement arguments)
    {
        var executable = BuiltinProcessRunner.NormalizeRequestedExecutable(
            BuiltinProcessToolExecutor.GetRequiredString(arguments, "fileName"));
        var processArguments = BuiltinProcessRunner.ParseArguments(arguments);
        var standardInput = BuiltinProcessRunner.ParseStandardInput(arguments);
        var environmentNames = BuiltinProcessRunner.ParseRequestedEnvironmentVariableNames(arguments);
        var workingDirectory = BuiltinFsPathPolicy.ResolveRequestedPath(
            workspaceRoot,
            BuiltinProcessToolExecutor.GetOptionalString(arguments, "workingDirectory")
                ?? workspaceRoot);
        return Create(
            $"process:{Path.GetFileName(executable)}",
            $"{processArguments.Length} arguments; stdin {standardInput?.Length ?? 0} characters",
            readRoots: [workingDirectory],
            allowedExecutables: [executable],
            allowedEnvironmentVariables: environmentNames);
    }

    private static ToolRequestedScope HttpScope(JsonElement arguments)
    {
        var method = BuiltinHttpToolExecutor.ParseMethod(
            BuiltinHttpToolExecutor.GetRequiredString(arguments, "method"));
        var uri = BuiltinHttpToolExecutor.ParseAndValidateRequestUri(
            BuiltinHttpToolExecutor.GetRequiredString(arguments, "url"));
        var headers = BuiltinHttpToolExecutor.ParseHeaders(arguments);
        var body = BuiltinHttpToolExecutor.GetOptionalString(arguments, "body");
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return Create(
            $"http:{uri.Scheme}://{uri.IdnHost}:{uri.Port}{path}",
            $"{method.Method}; {headers.Count} headers; body {body?.Length ?? 0} characters",
            networkPolicy: new NetworkPolicy(
                DenyAll: false,
                AllowedHosts: [uri.IdnHost],
                AllowedSchemes: [uri.Scheme],
                AllowedPorts: [uri.Port],
                AllowedAddressRanges: null,
                MaxRedirects: 0,
                MaxResponseBytes: DefaultMaxHttpResponseBytes));
    }

    private static string ResolvePath(
        string workspaceRoot,
        JsonElement arguments,
        string propertyName) =>
        BuiltinFsPathPolicy.ResolveRequestedPath(
            workspaceRoot,
            BuiltinFsToolExecutor.GetRequiredString(arguments, propertyName));

    private static string JoinTargets(string workspaceRoot, string source, string destination) =>
        $"{BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, source)} -> " +
        BuiltinFsPathPolicy.GetRequestedTargetSummary(workspaceRoot, destination);

    private static ToolRequestedScope Create(
        string targetSummary,
        string detailSummary,
        string[]? readRoots = null,
        string[]? writeRoots = null,
        bool allowOverwrite = false,
        bool allowMove = false,
        bool allowDelete = false,
        string[]? allowedExecutables = null,
        string[]? allowedEnvironmentVariables = null,
        bool allowPowerShell = false,
        NetworkPolicy? networkPolicy = null,
        bool canGrantForRun = false) =>
        new(
            targetSummary,
            detailSummary,
            Normalize(readRoots),
            Normalize(writeRoots),
            allowOverwrite,
            allowMove,
            allowDelete,
            Normalize(allowedExecutables),
            Normalize(allowedEnvironmentVariables),
            allowPowerShell,
            networkPolicy ?? new NetworkPolicy(true, []),
            canGrantForRun);

    private static string[] Normalize(string[]? values) =>
        values?.Distinct(PathComparer).Order(PathComparer).ToArray() ?? [];

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
