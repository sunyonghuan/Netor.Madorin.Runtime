using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// Unit tests for BuiltinDelegateAgentToolExecutor.
/// </summary>
[TestClass]
public sealed class BuiltinDelegateAgentToolExecutorTests
{
    [TestMethod]
    public void CanExecute_WithDelegateAgentToolId_ReturnsTrue()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();

        // Act
        var result = executor.CanExecute(BuiltinToolRegistry.DelegateToAgentToolId);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void CanExecute_WithOtherToolId_ReturnsFalse()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();

        // Act
        var result = executor.CanExecute("mcli_other_tool");

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithValidInput_ReturnsSuccess()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-001",
            ToolId: BuiltinToolRegistry.DelegateToAgentToolId,
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"agentName":"Developer","task":"Fix the bug in authentication"}""",
            RunId: "run-001",
            SessionId: "session-001");

        // Act
        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.OutputJson);
        StringAssert.Contains(result.OutputJson, "\"delegated\":true");
        StringAssert.Contains(result.OutputJson, "Developer");
        StringAssert.Contains(result.OutputJson, "Fix the bug in authentication");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMissingAgentName_ReturnsError()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-002",
            ToolId: BuiltinToolRegistry.DelegateToAgentToolId,
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"task":"Do something"}""",
            RunId: "run-001",
            SessionId: "session-001");

        // Act
        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        // Assert
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "agentName");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMissingTask_ReturnsError()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-003",
            ToolId: BuiltinToolRegistry.DelegateToAgentToolId,
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"agentName":"Developer"}""",
            RunId: "run-001",
            SessionId: "session-001");

        // Act
        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        // Assert
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "task");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithEmptyAgentName_ReturnsError()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-004",
            ToolId: BuiltinToolRegistry.DelegateToAgentToolId,
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"agentName":"","task":"Do something"}""",
            RunId: "run-001",
            SessionId: "session-001");

        // Act
        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        // Assert
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Agent name cannot be empty");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithEmptyTask_ReturnsError()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-005",
            ToolId: BuiltinToolRegistry.DelegateToAgentToolId,
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"agentName":"Developer","task":""}""",
            RunId: "run-001",
            SessionId: "session-001");

        // Act
        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);

        // Assert
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Task description cannot be empty");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithWrongToolId_ThrowsInvalidOperationException()
    {
        // Arrange
        var executor = new BuiltinDelegateAgentToolExecutor();
        var invocation = new ToolInvocation(
            CallId: "call-006",
            ToolId: "mcli_other_tool",
            AgentId: "agent-001",
            ParentAgentId: null,
            ArgumentsJson: """{"agentName":"Developer","task":"Fix bug"}""",
            RunId: "run-001",
            SessionId: "session-001");

        // Act & Assert
        try
        {
            await executor.ExecuteAsync(invocation, CancellationToken.None);
            Assert.Fail("Expected InvalidOperationException was not thrown");
        }
        catch (InvalidOperationException)
        {
            // Expected exception
        }
    }
}
