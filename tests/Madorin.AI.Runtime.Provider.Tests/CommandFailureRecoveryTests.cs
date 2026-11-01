using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>Verifies command input limits remain recoverable inside the same Run.</summary>
[TestClass]
public sealed class CommandFailureRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("script")]
    [DataRow("unicode-script")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OversizedCommand_ReturnsFailureToProviderAndAllowsCorrectedCall(string input)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "command-recovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var provider = new RecoveringCommandProvider(input);
            var consent = new CommandConsentHandler();
            await using var runtime = await LocalRuntime.StartAsync(
                new LocalRuntimeOptions(root, Path.Combine(root, "data"), Path.Combine(root, "logs"),
                    Guid.NewGuid().ToString("N"), _ => provider)
                {
                    MemoryUserHome = Path.Combine(root, "home"),
                    ToolConsentHandler = consent
                }, TestContext.CancellationToken);
            var selection = new NextTurnSelection(1, RuntimeMode.Expert, new("recovery", "test"),
                new ExpertModeOptions(new AgentRef("agent", "1", "Test command recovery.", "recovery", "test")));
            var request = new NewSessionRunRequest("request", "turn", RuntimeMode.Expert, selection,
                [new TextContentBlock("Run a command and recover from invalid input.")]);
            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in runtime.RunAsync(request, TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            var failures = events.Where(e => e.MessageType is MessageTypes.RunFailed or MessageTypes.InvocationFailed);
            Assert.IsEmpty(failures, string.Join(" | ", failures.Select(e => e.Payload.GetRawText())));
            Assert.ContainsSingle(events.Where(e => e.MessageType == MessageTypes.RunCompleted));
            Assert.AreEqual(3, provider.CallCount);
            Assert.IsTrue(provider.ObservedFailure);
            Assert.IsTrue(provider.ObservedRecovery);
            Assert.AreEqual(1, consent.PermissionCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Cleanup(root);
        }
    }

    private static void Cleanup(string root)
    {
        if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test cleanup directory.");
        }

        Directory.Delete(root, recursive: true);
    }

    private sealed class CommandConsentHandler : IToolConsentHandler
    {
        public int PermissionCount { get; private set; }

        public Task<ToolPermissionResponse> RequestPermissionAsync(ToolPermissionConsentContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PermissionCount++;
            var grantId = Guid.NewGuid().ToString("N");
            var grant = new ToolGrant(grantId, context.Invocation.RunId, context.WorkspaceRoot,
                AllowedReadRoots: [], AllowedWriteRoots: [], AllowOverwrite: false, AllowMove: false,
                AllowDelete: false, AllowedExecutables: [], AllowPowerShell: false,
                new NetworkPolicy(DenyAll: true, AllowedHosts: []), DateTimeOffset.UtcNow.AddHours(1),
                AllowDelegation: false, DelegatedAgentIds: [], AllowedToolIds: [BuiltinToolRegistry.CommandToolId],
                MaximumRisk: ToolRiskLevel.Process, AgentId: context.Invocation.AgentId, RootGrantId: grantId,
                CommandWorkingDirectory: context.ScopeResolution.Scope!.CommandWorkingDirectory);
            return Task.FromResult(new ToolPermissionResponse(context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId, ToolAuthorizationDecision.Granted, grant, "Approved for test."));
        }

        public Task<ApprovalResponse> RequestApprovalAsync(ToolApprovalConsentContext context, CancellationToken ct) =>
            Task.FromException<ApprovalResponse>(new NotSupportedException());
    }

    private sealed class RecoveringCommandProvider(string input) : IRuntimeProviderAdapter
    {
        public string ProviderId => "recovery";
        public ProviderCapabilities Capabilities { get; } = new(Streaming: true, ToolCalling: true);
        public IReadOnlyList<ProviderModel> Models { get; } = [new("test", "Test", ContextWindow: 2_000_000)];
        public int CallCount { get; private set; }
        public bool ObservedFailure { get; private set; }
        public bool ObservedRecovery { get; private set; }

        public Task ValidateCapabilitiesAsync(ContentBlock[] content, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var results = request.Messages.Where(m => m.Role == RuntimeProviderRoles.Tool)
                .SelectMany(m => m.Content).OfType<ToolResultContentBlock>().ToArray();
            if (CallCount == 2)
            {
                var failure = Assert.ContainsSingle(results);
                Assert.IsFalse(failure.Success);
                var error = string.Concat(failure.Content.OfType<TextContentBlock>().Select(t => t.Text));
                StringAssert.Contains(error, "8 KB limit");
                StringAssert.Contains(error, "mcli_fs_write");
                StringAssert.Contains(error, "stdin");
                ObservedFailure = true;
            }
            else if (CallCount == 3)
            {
                Assert.HasCount(2, results);
                Assert.IsTrue(results[1].Success);
                using var output = JsonDocument.Parse(Assert.ContainsSingle(results[1].Content.OfType<TextContentBlock>()).Text);
                Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
                StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "recovered");
                ObservedRecovery = true;
                yield return new TextDeltaProviderEvent(request.InvocationId, "Recovered.");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            Assert.IsLessThanOrEqualTo(2, CallCount);
            var arguments = new JsonObject
            {
                ["shell"] = OperatingSystem.IsWindows() ? "powershell" : "sh",
                ["script"] = CallCount == 1
                    ? new string(input == "unicode-script" ? '\u4e2d' : 'a', input == "unicode-script" ? 2731 : 8193)
                    : "echo recovered"
            };
            yield return new ToolCallCompleteProviderEvent(request.InvocationId, $"command-{CallCount}",
                BuiltinToolRegistry.CommandToolId, "command", arguments.ToJsonString());
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "tool_calls");
        }
    }
}
