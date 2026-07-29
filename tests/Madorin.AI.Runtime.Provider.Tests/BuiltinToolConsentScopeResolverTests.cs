using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class BuiltinToolConsentScopeResolverTests
{
    private string _workspace = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            "madorin-consent-scope-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [TestMethod]
    public void Resolve_FileReadWithChineseAndSpaces_UsesExactNormalizedTarget()
    {
        var relativePath = Path.Combine("资料 目录", "说明 文件.txt");
        var fullPath = Path.Combine(_workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "content");
        var jsonPath = relativePath.Replace('\\', '/');

        var resolution = Resolve(
            BuiltinToolRegistry.FileReadToolId,
            $$"""{"path":"{{jsonPath}}"}""");

        Assert.IsTrue(resolution.IsSuccess, resolution.Error);
        Assert.IsNotNull(resolution.Scope);
        Assert.AreEqual("workspace:资料 目录/说明 文件.txt", resolution.Scope.TargetSummary);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(fullPath) },
            resolution.Scope.AllowedReadRoots);
        Assert.IsTrue(resolution.Scope.CanGrantForRun);
    }

    [TestMethod]
    public void Resolve_NewFileWrite_UsesExactTargetAndAllowsRunGrant()
    {
        var target = Path.Combine(_workspace, "output", "result.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var resolution = Resolve(
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"output/result.txt","content":"secret","encoding":"utf-8","mode":"create"}""");

        Assert.IsTrue(resolution.IsSuccess, resolution.Error);
        Assert.IsNotNull(resolution.Scope);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(target) },
            resolution.Scope.AllowedWriteRoots);
        Assert.IsFalse(resolution.Scope.AllowOverwrite);
        Assert.IsTrue(resolution.Scope.CanGrantForRun);
        Assert.DoesNotContain("secret", resolution.Scope.DetailSummary, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Resolve_ExistingFileWrite_IsOverwriteAndAllowsRunGrant()
    {
        var target = Path.Combine(_workspace, "existing.txt");
        File.WriteAllText(target, "existing");

        var resolution = Resolve(
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"existing.txt","content":"replacement","encoding":"utf-8","mode":"overwrite","expectedHash":"0000000000000000000000000000000000000000000000000000000000000000"}""");

        Assert.IsTrue(resolution.IsSuccess, resolution.Error);
        Assert.IsNotNull(resolution.Scope);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(target) },
            resolution.Scope.AllowedWriteRoots);
        Assert.IsTrue(resolution.Scope.AllowOverwrite);
        Assert.IsTrue(resolution.Scope.CanGrantForRun);
    }

    [TestMethod]
    [DataRow(BuiltinToolRegistry.FileReadToolId, "{\"path\":\"../outside.txt\"}")]
    [DataRow(BuiltinToolRegistry.FileReadToolId, "{\"path\":\"\\\\server\\share\\file.txt\"}")]
    [DataRow(BuiltinToolRegistry.FileReadToolId, "{\"path\":\"\\\\?\\C:\\Windows\\win.ini\"}")]
    [DataRow(BuiltinToolRegistry.ProcessRunToolId, "{\"fileName\":\"tools/dotnet\"}")]
    [DataRow(BuiltinToolRegistry.HttpRequestToolId, "{\"method\":\"GET\",\"url\":\"https://user:password@example.test/data\"}")]
    public void Resolve_UnsafeOrDynamicTarget_FailsClosed(string toolId, string argumentsJson)
    {
        var resolution = Resolve(toolId, argumentsJson);

        Assert.IsFalse(resolution.IsSuccess);
        Assert.IsNull(resolution.Scope);
        Assert.IsFalse(string.IsNullOrWhiteSpace(resolution.Error));
    }

    [TestMethod]
    public void Resolve_UnknownBuiltinTool_ReturnsStructuredFailure()
    {
        var descriptor = new ToolDescriptor(
            "builtin.unknown",
            "builtin",
            "Unknown",
            "Unknown tool.",
            "{}",
            ExecutionTarget: ToolExecutionTarget.Builtin);
        var invocation = CreateInvocation(descriptor.ToolId, "{}");

        var resolution = new BuiltinToolConsentScopeResolver().Resolve(
            invocation,
            descriptor,
            _workspace);

        Assert.IsFalse(resolution.IsSuccess);
        Assert.IsNull(resolution.Scope);
    }

    [TestMethod]
    public void Resolve_SkillScript_OnlyAllowsRuntimeInterpreterEnvironment()
    {
        var invocation = CreateInvocation(
            SkillScriptToolDefinition.Descriptor.ToolId,
            """{"skillName":"demo","script":"scripts/run.cs","scriptHash":"ABC","interpreter":"dotnet","catalogVersion":"v-1"}""");

        var resolution = new BuiltinToolConsentScopeResolver().Resolve(
            invocation,
            SkillScriptToolDefinition.Descriptor,
            _workspace);

        Assert.IsTrue(resolution.IsSuccess, resolution.Error);
        Assert.IsNotNull(resolution.Scope);
        CollectionAssert.AreEquivalent(
            SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray(),
            resolution.Scope.AllowedEnvironmentVariables);
        Assert.DoesNotContain(
            "OPENAI_API_KEY",
            resolution.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Resolve_ProcessUsesSafeEnvironmentAndRestrictsCustomVariablesToOnce()
    {
        var process = Resolve(
            BuiltinToolRegistry.ProcessRunToolId,
            """{"fileName":"dotnet","arguments":["--info","sensitive-value"],"stdin":"secret-input","environment":{"TOKEN":"secret-token"}}""");
        var safeProcess = Resolve(
            BuiltinToolRegistry.ProcessRunToolId,
            """{"fileName":"dotnet","arguments":["--info"]}""");
        var http = Resolve(
            BuiltinToolRegistry.HttpRequestToolId,
            """{"method":"POST","url":"https://example.test:8443/orders","headers":{"Authorization":"Bearer secret-token"},"body":"secret-body"}""");

        Assert.IsTrue(process.IsSuccess, process.Error);
        Assert.IsNotNull(process.Scope);
        Assert.HasCount(1, process.Scope.AllowedExecutables);
        Assert.AreEqual("dotnet", process.Scope.AllowedExecutables[0]);
        Assert.Contains(
            "TOKEN",
            process.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            "TEMP",
            process.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            "TMP",
            process.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            "USERPROFILE",
            process.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "OPENAI_API_KEY",
            process.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("2 arguments; stdin 12 characters", process.Scope.DetailSummary);
        Assert.IsFalse(process.Scope.CanGrantForRun);
        Assert.DoesNotContain("sensitive-value", process.Scope.DetailSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", process.Scope.DetailSummary, StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(safeProcess.IsSuccess, safeProcess.Error);
        Assert.IsNotNull(safeProcess.Scope);
        Assert.IsTrue(safeProcess.Scope.CanGrantForRun);
        Assert.Contains(
            "TEMP",
            safeProcess.Scope.AllowedEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(http.IsSuccess, http.Error);
        Assert.IsNotNull(http.Scope);
        Assert.AreEqual("http:https://example.test:8443/orders", http.Scope.TargetSummary);
        Assert.AreEqual("POST; 1 headers; body 11 characters", http.Scope.DetailSummary);
        Assert.IsFalse(http.Scope.CanGrantForRun);
        Assert.IsFalse(http.Scope.NetworkPolicy.DenyAll);
        Assert.HasCount(1, http.Scope.NetworkPolicy.AllowedHosts);
        Assert.AreEqual("example.test", http.Scope.NetworkPolicy.AllowedHosts[0]);
        Assert.IsNotNull(http.Scope.NetworkPolicy.AllowedPorts);
        Assert.HasCount(1, http.Scope.NetworkPolicy.AllowedPorts);
        Assert.AreEqual(8443, http.Scope.NetworkPolicy.AllowedPorts[0]);
        Assert.DoesNotContain("secret", http.Scope.DetailSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", http.Scope.DetailSummary, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Resolve_MemoryReadAllowsRunAndAllMemoryWritesAreOnceOnly()
    {
        var read = Resolve(
            BuiltinToolRegistry.MemoryReadToolId,
            """{"scope":"project"}""");
        var append = Resolve(
            BuiltinToolRegistry.MemoryAppendToolId,
            """{"scope":"project","lines":["- never print this memory value"],"expectedHash":null}""");
        var update = Resolve(
            BuiltinToolRegistry.MemoryUpdateToolId,
            """{"scope":"project","startLine":1,"endLine":1,"lines":["- changed"],"expectedHash":"hash"}""");
        var delete = Resolve(
            BuiltinToolRegistry.MemoryDeleteToolId,
            """{"scope":"project","startLine":1,"endLine":2,"expectedHash":"hash"}""");

        Assert.IsTrue(read.IsSuccess, read.Error);
        Assert.IsNotNull(read.Scope);
        Assert.AreEqual("memory:project", read.Scope.TargetSummary);
        Assert.IsTrue(read.Scope.CanGrantForRun);

        Assert.IsTrue(append.IsSuccess, append.Error);
        Assert.IsNotNull(append.Scope);
        Assert.AreEqual("memory:project", append.Scope.TargetSummary);
        Assert.AreEqual("append memory lines", append.Scope.DetailSummary);
        Assert.IsFalse(append.Scope.CanGrantForRun);
        Assert.DoesNotContain("never print", append.Scope.DetailSummary, StringComparison.Ordinal);
        Assert.IsTrue(update.IsSuccess, update.Error);
        Assert.AreEqual("memory:project", update.Scope!.TargetSummary);
        Assert.IsFalse(update.Scope.CanGrantForRun);
        Assert.IsTrue(delete.IsSuccess, delete.Error);
        Assert.AreEqual("memory:project", delete.Scope!.TargetSummary);
        Assert.IsFalse(delete.Scope.CanGrantForRun);
    }

    [TestMethod]
    public void Resolve_DestructiveFileOperations_AreExactAndOnceOnly()
    {
        var source = Path.Combine(_workspace, "source.txt");
        File.WriteAllText(source, "source");
        var move = Resolve(
            BuiltinToolRegistry.FileMoveToolId,
            """{"source":"source.txt","destination":"moved.txt"}""");
        var delete = Resolve(
            BuiltinToolRegistry.FileDeleteToolId,
            """{"path":"source.txt"}""");
        var patch = Resolve(
            BuiltinToolRegistry.FileEditToolId,
            """{"path":"source.txt","encoding":"utf-8","expectedHash":"hash","operations":[{"type":"replace","startLine":1,"endLine":1,"content":"secret"}]}""");

        Assert.IsTrue(move.IsSuccess, move.Error);
        Assert.IsNotNull(move.Scope);
        CollectionAssert.AreEquivalent(
            new[]
            {
                Path.GetFullPath(source),
                Path.GetFullPath(Path.Combine(_workspace, "moved.txt"))
            },
            move.Scope.AllowedWriteRoots);
        Assert.IsTrue(move.Scope.AllowMove);
        Assert.IsFalse(move.Scope.CanGrantForRun);

        Assert.IsTrue(delete.IsSuccess, delete.Error);
        Assert.IsNotNull(delete.Scope);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(source) },
            delete.Scope.AllowedWriteRoots);
        Assert.IsTrue(delete.Scope.AllowDelete);
        Assert.IsTrue(delete.Scope.CanGrantForRun);

        Assert.IsTrue(patch.IsSuccess, patch.Error);
        Assert.IsNotNull(patch.Scope);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(source) },
            patch.Scope.AllowedReadRoots);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(source) },
            patch.Scope.AllowedWriteRoots);
        Assert.IsTrue(patch.Scope.AllowOverwrite);
        Assert.IsTrue(patch.Scope.CanGrantForRun);
        Assert.DoesNotContain("secret", patch.Scope.DetailSummary, StringComparison.Ordinal);
    }

    private ToolConsentScopeResolution Resolve(string toolId, string argumentsJson)
    {
        var descriptor = new BuiltinToolRegistry().GetTools()
            .Single(tool => string.Equals(tool.ToolId, toolId, StringComparison.Ordinal));
        return new BuiltinToolConsentScopeResolver().Resolve(
            CreateInvocation(toolId, argumentsJson),
            descriptor,
            _workspace);
    }

    private static ToolInvocation CreateInvocation(string toolId, string argumentsJson) =>
        new(
            "call-1",
            toolId,
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            "invocation-1");

}
