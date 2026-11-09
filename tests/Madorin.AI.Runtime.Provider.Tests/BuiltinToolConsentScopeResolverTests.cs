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
    public void Resolve_AttachmentUsesExistingReadGrantScope()
    {
        File.WriteAllText(Path.Join(_workspace, "data.bin"), "content");
        var resolution = Resolve(BuiltinToolRegistry.FileAttachToolId, """{"path":"data.bin"}""");
        Assert.IsTrue(resolution.IsSuccess, resolution.Error);
        Assert.IsNotNull(resolution.Scope);
        CollectionAssert.AreEqual(new[] { Path.Join(_workspace, "data.bin") }, resolution.Scope.AllowedReadRoots);
        Assert.IsEmpty(resolution.Scope.AllowedWriteRoots);
        Assert.IsTrue(resolution.Scope.CanGrantForRun);
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
    [DataRow(BuiltinToolRegistry.CommandToolId, "{\"fileName\":\"tools/dotnet\"}")]
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
    public void Resolve_CommandAuthorizationOnlyBindsWorkingDirectory()
    {
        var process = Resolve(
            BuiltinToolRegistry.CommandToolId,
            """{"script":"dotnet --info","shell":"auto","stdin":"secret-input","environment":{"TOKEN":"secret-token"}}""");
        var safeProcess = Resolve(
            BuiltinToolRegistry.CommandToolId,
            """{"script":"dotnet --info"}""");
        var http = Resolve(
            BuiltinToolRegistry.HttpRequestToolId,
            """{"method":"POST","url":"https://example.test:8443/orders","headers":{"Authorization":"Bearer secret-token"},"body":"secret-body"}""");

        Assert.IsTrue(process.IsSuccess, process.Error);
        Assert.IsNotNull(process.Scope);
        Assert.IsEmpty(process.Scope.AllowedExecutables);
        Assert.IsEmpty(process.Scope.AllowedEnvironmentVariables);
        Assert.IsEmpty(process.Scope.AllowedReadRoots);
        Assert.AreEqual(CommandToolAuthorization.NormalizeDirectory(_workspace), process.Scope.CommandWorkingDirectory);
        StringAssert.Contains(process.Scope.DetailSummary, "execute any commands with your user permissions");
        Assert.IsTrue(process.Scope.CanGrantForRun);
        Assert.DoesNotContain("sensitive-value", process.Scope.DetailSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", process.Scope.DetailSummary, StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(safeProcess.IsSuccess, safeProcess.Error);
        Assert.IsNotNull(safeProcess.Scope);
        Assert.IsTrue(safeProcess.Scope.CanGrantForRun);
        Assert.AreEqual(process.Scope.CommandWorkingDirectory, safeProcess.Scope.CommandWorkingDirectory);

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

        var write = Resolve(
            BuiltinToolRegistry.MemoryWriteToolId,
            """{"scope":"project","operation":"append","lines":["- never print this memory value"],"expectedHash":null}""");
        Assert.IsTrue(write.IsSuccess, write.Error);
        Assert.AreEqual("append memory lines", write.Scope!.DetailSummary);
        Assert.IsFalse(write.Scope.CanGrantForRun);
        Assert.DoesNotContain("never print", write.Scope.DetailSummary, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Resolve_InspectAndMergedWrite_MatchLegacyScopes()
    {
        var list = Resolve(
            BuiltinToolRegistry.FileInspectToolId,
            """{"mode":"list","path":"."}""");
        var stat = Resolve(
            BuiltinToolRegistry.FileInspectToolId,
            """{"mode":"stat","path":"existing.txt"}""");
        File.WriteAllText(Path.Combine(_workspace, "existing.txt"), "existing");
        var append = Resolve(
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"existing.txt","mode":"append","content":"more","encoding":"utf-8","expectedHash":"hash"}""");
        var mkdir = Resolve(
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"docs","mode":"mkdir"}""");

        Assert.IsTrue(list.IsSuccess, list.Error);
        Assert.AreEqual("directory listing", list.Scope!.DetailSummary);
        Assert.IsTrue(stat.IsSuccess, stat.Error);
        Assert.AreEqual("path metadata", stat.Scope!.DetailSummary);
        Assert.IsTrue(append.IsSuccess, append.Error);
        Assert.AreEqual("append text", append.Scope!.DetailSummary);
        Assert.IsTrue(append.Scope.AllowOverwrite);
        Assert.IsTrue(mkdir.IsSuccess, mkdir.Error);
        Assert.AreEqual("create directory", mkdir.Scope!.DetailSummary);
    }

    [TestMethod]
    public void Resolve_ExpertPlanTools_AreRunScopedWithoutFilesystemPermissions()
    {
        var toolIds = new[]
        {
            BuiltinToolRegistry.ExpertCreatePlanToolId,
            BuiltinToolRegistry.ExpertExecuteStepToolId,
            BuiltinToolRegistry.ExpertGetPlanToolId,
            BuiltinToolRegistry.ExpertUpdateStepStatusToolId,
            BuiltinToolRegistry.ExpertAbandonPlanToolId
        };

        foreach (var toolId in toolIds)
        {
            var resolution = Resolve(toolId, "{}");

            Assert.IsTrue(resolution.IsSuccess, $"{toolId}: {resolution.Error}");
            Assert.IsNotNull(resolution.Scope);
            Assert.AreEqual("expert:plan", resolution.Scope.TargetSummary);
            Assert.IsTrue(resolution.Scope.CanGrantForRun);
            Assert.IsEmpty(resolution.Scope.AllowedReadRoots);
            Assert.IsEmpty(resolution.Scope.AllowedWriteRoots);
        }
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
