using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// Unit tests for <see cref="BuiltinDelegateAgentToolExecutor"/>.
/// </summary>
[TestClass]
public sealed class BuiltinDelegateAgentToolExecutorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void CanExecute_WithDelegateAgentToolId_ReturnsTrue()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();

        var result = executor.CanExecute(BuiltinToolRegistry.DelegateToAgentToolId);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void CanExecute_WithOtherToolId_ReturnsFalse()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();

        var result = executor.CanExecute("mcli_other_tool");

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithValidInput_ReturnsSuccess()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = CreateInvocation(
            """{"agentName":"Developer","task":"Fix the bug in authentication"}""");

        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.OutputJson);
        StringAssert.Contains(result.OutputJson, "\"delegated\":true");
        StringAssert.Contains(result.OutputJson, "Developer");
        StringAssert.Contains(result.OutputJson, "Fix the bug in authentication");
        StringAssert.Contains(result.OutputJson, "Task delegation request accepted");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMissingAgentName_ReturnsError()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = CreateInvocation("""{"task":"Do something"}""");

        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "agentName");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMissingTask_ReturnsError()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = CreateInvocation("""{"agentName":"Developer"}""");

        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "task");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithEmptyAgentName_ReturnsError()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = CreateInvocation("""{"agentName":"","task":"Do something"}""");

        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Agent name cannot be empty");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithEmptyTask_ReturnsError()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = CreateInvocation("""{"agentName":"Developer","task":""}""");

        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Task description cannot be empty");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithWrongToolId_ThrowsInvalidOperationException()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-006",
            ToolId: "mcli_other_tool",
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"agentName":"Developer","task":"Fix bug"}""",
            RunId: "run-001",
            SessionId: "session-001");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(invocation, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExecuteAsync_WithInvalidJson_ReturnsError()
    {
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = CreateInvocation("{not-json");

        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));
    }

    [TestMethod]
    public async Task Gateway_WithDelegateExecutor_DoesNotReportExecutorUnavailable()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"madorin-delegate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.CancellationToken);
            await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
            using var repository = new SqliteToolIntentRepository(connection);
            using var outbox = new SqliteEventOutbox(connection);
            var registry = new BuiltinToolRegistry();
            var validator = new JsonSchemaToolValidator();
            using var gateway = new ToolGateway(
                new ToolCatalogStore(registry, validator),
                validator,
                new ToolAuthorizationService(
                    repository,
                    RuntimeToolPolicy.CreateRestricted(workspace)),
                [new BuiltinDelegateAgentToolExecutor()],
                repository,
                outbox);

            var result = await gateway.ExecuteAsync(
                CreateInvocation("""{"agentName":"Developer","task":"sum 1 to 10"}"""),
                permission: null,
                TestContext.CancellationToken);

            Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
            Assert.IsNotNull(result.Result);
            Assert.IsTrue(result.Result.Success);
            Assert.IsFalse(
                string.Equals(
                    result.Error?.Code,
                    RuntimeErrorCodes.ToolExecutorUnavailable,
                    StringComparison.Ordinal));
            StringAssert.Contains(result.Result.OutputJson, "\"delegated\":true");
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static ToolInvocation CreateInvocation(string argumentsJson) =>
        new(
            CallId: "call-001",
            ToolId: BuiltinToolRegistry.DelegateToAgentToolId,
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: argumentsJson,
            RunId: "run-001",
            SessionId: "session-001");
}
