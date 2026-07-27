using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class MemoryToolTests
{
    private static readonly string?[] AppendScopes = ["global", "project"];
    private static readonly string?[] ReadScopes = ["global", "project"];
    private static readonly string[] UpdateRequired = ["scope", "startLine", "endLine", "lines", "expectedHash"];
    private static readonly string[] DeleteRequired = ["scope", "startLine", "endLine", "expectedHash"];

    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            $"madorin-memory-tool-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Registry_MemoryTools_FreezeIdsSchemasAndApprovalRequirement()
    {
        var registry = new BuiltinToolRegistry();

        var read = Assert.ContainsSingle(registry.GetTools(
            [BuiltinToolRegistry.MemoryReadToolId]));
        var append = Assert.ContainsSingle(registry.GetTools(
            [BuiltinToolRegistry.MemoryAppendToolId]));
        var update = Assert.ContainsSingle(registry.GetTools(
            [BuiltinToolRegistry.MemoryUpdateToolId]));
        var delete = Assert.ContainsSingle(registry.GetTools(
            [BuiltinToolRegistry.MemoryDeleteToolId]));

        Assert.IsFalse(read.RequiresApproval);
        Assert.IsTrue(append.RequiresApproval);
        Assert.IsTrue(update.RequiresApproval);
        Assert.IsTrue(delete.RequiresApproval);
        using var readInput = JsonDocument.Parse(read.InputSchemaJson);
        using var readOutput = JsonDocument.Parse(read.OutputSchemaJson);
        using var appendInput = JsonDocument.Parse(append.InputSchemaJson);
        using var appendOutput = JsonDocument.Parse(append.OutputSchemaJson);
        using var updateInput = JsonDocument.Parse(update.InputSchemaJson);
        using var deleteInput = JsonDocument.Parse(delete.InputSchemaJson);
        CollectionAssert.AreEquivalent(
            ReadScopes,
            readInput.RootElement
                .GetProperty("properties")
                .GetProperty("scope")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.IsTrue(readOutput.RootElement
            .GetProperty("properties")
            .TryGetProperty("hash", out _));
        CollectionAssert.AreEquivalent(
            AppendScopes,
            appendInput.RootElement
                .GetProperty("properties")
                .GetProperty("scope")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.AreEqual(
            "nextInvocation",
            appendOutput.RootElement
                .GetProperty("properties")
                .GetProperty("appliesFrom")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEquivalent(
            UpdateRequired,
            updateInput.RootElement
                .GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        CollectionAssert.AreEquivalent(
            DeleteRequired,
            deleteInput.RootElement
                .GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_Read_ReturnsCurrentLinesAndHashFromFixedScope()
    {
        var service = CreateMemoryService();
        const string content = "# Memory\n\n- Read this rule.\n";
        await service.ReplaceAsync(
            MemoryScope.Project,
            content,
            TestContext.CancellationToken);
        await service.ReplaceAsync(
            MemoryScope.Project,
            "# Memory\n\n- A newer rule.\n",
            TestContext.CancellationToken);
        var executor = new BuiltinMemoryToolExecutor(service);
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryReadToolId,
            "{\"scope\":\"project\"}");

        var result = await executor.ExecuteAsync(
            invocation,
            TestContext.CancellationToken);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(3, output.RootElement.GetProperty("lineCount").GetInt32());
        Assert.AreEqual(
            "- A newer rule.",
            output.RootElement.GetProperty("lines")[2].GetProperty("text").GetString());
        Assert.AreEqual(
            await service.GetHashAsync(
                MemoryScope.Project,
                TestContext.CancellationToken),
            output.RootElement.GetProperty("hash").GetString());
    }

    [TestMethod]
    public async Task ExecuteAsync_AppendWithoutApproval_DoesNotWrite()
    {
        var service = CreateMemoryService();
        var executor = new BuiltinMemoryToolExecutor(
            service,
            new MemoryInvocationSnapshotStore());
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryAppendToolId,
            "{\"scope\":\"global\",\"lines\":[\"- Do not write\"],\"expectedHash\":null}");

        var result = await executor.ExecuteAsync(
            invocation,
            TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "approval");
        Assert.IsFalse(File.Exists(service.GlobalPath));
    }

    [TestMethod]
    public async Task Gateway_ApprovedAppend_RoutesLocallyAndReturnsNextInvocationHash()
    {
        var service = CreateMemoryService();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var registry = new FakeMemoryToolRegistry();
        var validator = new JsonSchemaToolValidator();
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(Path.Join(_root, "workspace"))),
            [new BuiltinMemoryToolExecutor(service, new MemoryInvocationSnapshotStore())],
            repository,
            outbox);
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryAppendToolId,
            "{\"scope\":\"global\",\"lines\":[\"- Approved rule\"],\"expectedHash\":null}");

        var unapproved = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            TestContext.CancellationToken);
        var approved = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(invocation.CallId),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.NeedsApproval, unapproved.Kind);
        Assert.AreEqual(ToolGatewayResultKind.Success, approved.Kind);
        Assert.IsNotNull(approved.Result);
        using var output = JsonDocument.Parse(approved.Result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("appended").GetBoolean());
        Assert.AreEqual(
            "nextInvocation",
            output.RootElement.GetProperty("appliesFrom").GetString());
        Assert.AreEqual(
            await service.GetHashAsync(
                MemoryScope.Global,
                TestContext.CancellationToken),
            output.RootElement.GetProperty("hash").GetString());
        StringAssert.Contains(
            await service.ReadAsync(
                MemoryScope.Global,
                TestContext.CancellationToken),
            "- Approved rule\n");
    }

    [TestMethod]
    public async Task ExecuteAsync_UpdateAndDelete_UseInclusiveLineRanges()
    {
        var service = CreateMemoryService();
        const string original = "# Memory\n- First\n- Second\n- Temporary\n";
        await service.ReplaceAsync(
            MemoryScope.Project,
            original,
            TestContext.CancellationToken);
        var executor = new BuiltinMemoryToolExecutor(service);
        var originalHash = await service.GetHashAsync(
            MemoryScope.Project,
            TestContext.CancellationToken);

        var updateInvocation = CreateInvocation(
            BuiltinToolRegistry.MemoryUpdateToolId,
            $"{{\"scope\":\"project\",\"startLine\":2,\"endLine\":3,\"lines\":[\"- Updated one\",\"- Updated two\"],\"expectedHash\":\"{originalHash}\"}}")
            with { PermissionContext = CreatePermission("update-call") };
        var updateResult = await executor.ExecuteAsync(
            updateInvocation,
            TestContext.CancellationToken);

        Assert.IsTrue(updateResult.Success, updateResult.Error);
        var updated = await service.ReadAsync(
            MemoryScope.Project,
            TestContext.CancellationToken);
        Assert.AreEqual("# Memory\n- Updated one\n- Updated two\n- Temporary\n", updated);

        var updateHash = await service.GetHashAsync(
            MemoryScope.Project,
            TestContext.CancellationToken);
        var deleteInvocation = CreateInvocation(
            BuiltinToolRegistry.MemoryDeleteToolId,
            $"{{\"scope\":\"project\",\"startLine\":2,\"endLine\":3,\"expectedHash\":\"{updateHash}\"}}")
            with { PermissionContext = CreatePermission("delete-call") };
        var deleteResult = await executor.ExecuteAsync(
            deleteInvocation,
            TestContext.CancellationToken);

        Assert.IsTrue(deleteResult.Success, deleteResult.Error);
        Assert.AreEqual(
            "# Memory\n- Temporary\n",
            await service.ReadAsync(MemoryScope.Project, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_UpdateWithStaleHash_RejectsWithoutWriting()
    {
        var service = CreateMemoryService();
        await service.ReplaceAsync(
            MemoryScope.Project,
            "# Memory\n- Original\n",
            TestContext.CancellationToken);
        var executor = new BuiltinMemoryToolExecutor(service);
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryUpdateToolId,
            "{\"scope\":\"project\",\"startLine\":2,\"endLine\":2,\"lines\":[\"- Changed\"],\"expectedHash\":\"stale\"}")
            with { PermissionContext = CreatePermission("update-call") };

        var result = await executor.ExecuteAsync(
            invocation,
            TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Read it again");
        Assert.AreEqual(
            "# Memory\n- Original\n",
            await service.ReadAsync(MemoryScope.Project, TestContext.CancellationToken));
    }

    private MemoryFileService CreateMemoryService() => new(
        Path.Join(_root, "home"),
        Path.Join(_root, "workspace"));

    private ToolPermissionContext CreatePermission(string callId) => new(
        "approval-1",
        "run-1",
        Path.Join(_root, "workspace"),
        [],
        [],
        AllowedCallIds: [callId],
        ApprovalRequestId: "approval-request-1");

    private static ToolInvocation CreateInvocation(string toolId, string argumentsJson) => new(
        Guid.NewGuid().ToString("N"),
        toolId,
        "agent-1",
        ParentAgentId: null,
        argumentsJson,
        "run-1",
        "session-1",
        InvocationId: "invocation-1");

    private sealed class FakeMemoryToolRegistry : IBuiltinToolRegistry
    {
        private static readonly string[] MemoryToolIds =
        [
            BuiltinToolRegistry.MemoryReadToolId,
            BuiltinToolRegistry.MemoryAppendToolId,
            BuiltinToolRegistry.MemoryUpdateToolId,
            BuiltinToolRegistry.MemoryDeleteToolId
        ];

        private readonly IReadOnlyList<ToolDescriptor> _tools =
            new BuiltinToolRegistry().GetTools(MemoryToolIds);

        public string CatalogVersion => "memory-test-v1";

        public IReadOnlyList<ToolDescriptor> GetTools() => _tools;

        public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
        {
            ArgumentNullException.ThrowIfNull(toolIds);
            var requested = toolIds.ToHashSet(StringComparer.Ordinal);
            return _tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
        }
    }
}
