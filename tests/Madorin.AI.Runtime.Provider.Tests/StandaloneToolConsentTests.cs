using System.Collections.Concurrent;
using System.Text;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Cli.Tools;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class StandaloneToolConsentTests
{
    private static readonly DateTimeOffset FixedNow = new(
        2026,
        7,
        26,
        8,
        0,
        0,
        TimeSpan.Zero);

    private string _workspace = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            "madorin-standalone-consent-tests",
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
    [DataRow(true, true, false, true)]
    [DataRow(false, true, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(true, true, true, false)]
    public void IsInteractiveConsentEnabled_RequiresInteractiveTextWithoutOutputFile(
        bool isInteractive,
        bool isTextOutput,
        bool hasOutputFile,
        bool expected)
    {
        var terminal = new ScriptedTerminal(isInteractive);

        var actual = StandaloneToolConsentPolicy.IsInteractiveConsentEnabled(
            terminal,
            isTextOutput,
            hasOutputFile);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Create_OnceGrantBindsRunAgentToolCallAndExactScope()
    {
        var context = CreatePermissionContext(canGrantForRun: true);
        var factory = new StandaloneToolGrantFactory(new FixedTimeProvider(FixedNow));

        var grant = factory.Create(context, StandaloneToolGrantLifetime.Once);

        Assert.AreEqual(context.Invocation.RunId, grant.RunId);
        Assert.AreEqual(context.Invocation.AgentId, grant.AgentId);
        Assert.IsNotNull(grant.AllowedToolIds);
        Assert.HasCount(1, grant.AllowedToolIds);
        Assert.AreEqual(context.Invocation.ToolId, grant.AllowedToolIds[0]);
        Assert.IsNotNull(grant.AllowedCallIds);
        Assert.HasCount(1, grant.AllowedCallIds);
        Assert.AreEqual(context.Invocation.CallId, grant.AllowedCallIds[0]);
        CollectionAssert.AreEqual(
            context.ScopeResolution.Scope!.AllowedReadRoots,
            grant.AllowedReadRoots);
        Assert.AreEqual(FixedNow.AddHours(4), grant.ExpiresAt);
        Assert.AreEqual(grant.GrantId, grant.RootGrantId);
        Assert.IsFalse(grant.AllowDelegation);
    }

    [TestMethod]
    public void Create_RunGrantOmitsCallBindingAndExpandsReadScopeToWorkspace()
    {
        var context = CreatePermissionContext(canGrantForRun: true);
        var factory = new StandaloneToolGrantFactory(new FixedTimeProvider(FixedNow));

        var grant = factory.Create(context, StandaloneToolGrantLifetime.Run);

        Assert.AreEqual(context.Invocation.RunId, grant.RunId);
        Assert.AreEqual(context.Invocation.AgentId, grant.AgentId);
        Assert.IsNull(grant.AllowedCallIds);
        Assert.IsNotNull(grant.AllowedToolIds);
        Assert.HasCount(1, grant.AllowedToolIds);
        Assert.AreEqual(context.Invocation.ToolId, grant.AllowedToolIds[0]);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(_workspace) },
            grant.AllowedReadRoots);
    }

    [TestMethod]
    public void Create_CommandRunGrant_BindsDirectoryWithoutFilesystemOrExecutablePermissions()
    {
        var descriptor = new BuiltinToolRegistry().GetTools().Single(tool => tool.ToolId == BuiltinToolRegistry.CommandToolId);
        var invocation = new ToolInvocation("command-call", descriptor.ToolId, "agent-1", null,
            """{"script":"echo command","workingDirectory":"nested","environment":{"CUSTOM":"value"}}""",
            "command-run", "command-session");
        var resolution = new BuiltinToolConsentScopeResolver().Resolve(invocation, descriptor, _workspace);
        var request = new ToolPermissionRequest("command-request", "agent-1", null, descriptor.ToolId,
            invocation.CallId, "hash", "Process", "call-or-run", "command permission");
        var context = new ToolPermissionConsentContext(request, invocation, descriptor, resolution, _workspace);
        var factory = new StandaloneToolGrantFactory();

        var grant = factory.Create(context, StandaloneToolGrantLifetime.Run);
        Assert.AreEqual(Path.Combine(_workspace, "nested"), grant.CommandWorkingDirectory);
        Assert.IsNull(grant.AllowedCallIds);
        Assert.IsEmpty(grant.AllowedReadRoots);
        Assert.IsEmpty(grant.AllowedWriteRoots);
        Assert.IsEmpty(grant.AllowedExecutables);
        Assert.IsEmpty(grant.AllowedEnvironmentVariables!);
        var once = factory.Create(context, StandaloneToolGrantLifetime.Once);
        Assert.AreEqual(grant.CommandWorkingDirectory, once.CommandWorkingDirectory);
        Assert.AreEqual(invocation.CallId, once.AllowedCallIds![0]);
    }

    [TestMethod]
    public async Task Create_RunFileWriteGrantAuthorizesAnotherWorkspaceFileWithoutDestructiveFlags()
    {
        var context = CreateWritePermissionContext("call-write-1", "src/new-file.cs");
        var factory = new StandaloneToolGrantFactory();

        var grant = factory.Create(context, StandaloneToolGrantLifetime.Run);

        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(_workspace) },
            grant.AllowedWriteRoots);
        Assert.IsTrue(grant.AllowOverwrite);
        Assert.IsFalse(grant.AllowMove);
        Assert.IsFalse(grant.AllowDelete);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var authorization = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        await authorization.SaveGrantAsync(grant, ct: TestContext.CancellationToken);
        var secondContext = CreateWritePermissionContext("call-write-2", "tests/other-file.cs");

        var secondAuthorization = await authorization.AuthorizeAsync(
            secondContext.Invocation,
            secondContext.Descriptor,
            ct: TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, secondAuthorization.Decision);
        Assert.IsNotNull(secondAuthorization.EffectivePermission);
    }

    [TestMethod]
    public void Create_RunDeleteGrantAllowsOnlyWorkspaceDeletesForCurrentRunAndAgent()
    {
        var context = CreateDeletePermissionContext("call-delete-1", "logs/001.log");
        var factory = new StandaloneToolGrantFactory(new FixedTimeProvider(FixedNow));

        var grant = factory.Create(context, StandaloneToolGrantLifetime.Run);

        Assert.AreEqual(context.Invocation.RunId, grant.RunId);
        Assert.AreEqual(context.Invocation.AgentId, grant.AgentId);
        Assert.IsNull(grant.AllowedCallIds);
        CollectionAssert.AreEqual(new[] { context.Invocation.ToolId }, grant.AllowedToolIds);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(_workspace) },
            grant.AllowedWriteRoots);
        Assert.IsTrue(grant.AllowDelete);
        Assert.IsFalse(grant.AllowDelegation);
    }

    [TestMethod]
    public void Create_RunGrantForOnceOnlyScope_Throws()
    {
        var context = CreatePermissionContext(canGrantForRun: false);
        var factory = new StandaloneToolGrantFactory(new FixedTimeProvider(FixedNow));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => factory.Create(context, StandaloneToolGrantLifetime.Run));
    }

    [TestMethod]
    public void TryParsePermissionMode_ParsesSupportedValues()
    {
        Assert.IsTrue(StandaloneToolConsentPolicy.TryParsePermissionMode(
            null,
            out var defaultMode));
        Assert.AreEqual(StandaloneToolPermissionMode.AskAll, defaultMode);
        Assert.IsTrue(StandaloneToolConsentPolicy.TryParsePermissionMode(
            "auto-tools",
            out var autoTools));
        Assert.AreEqual(StandaloneToolPermissionMode.AutoTools, autoTools);
        Assert.IsTrue(StandaloneToolConsentPolicy.TryParsePermissionMode(
            "AUTO-ALL",
            out var autoAll));
        Assert.AreEqual(StandaloneToolPermissionMode.AutoAll, autoAll);
        Assert.IsFalse(StandaloneToolConsentPolicy.TryParsePermissionMode(
            "auto",
            out _));
    }

    [TestMethod]
    [DataRow("auto-tools")]
    [DataRow("auto-all")]
    public async Task RequestPermissionAsync_AutoModesSkipPromptAndCreateOnceGrant(
        string modeValue)
    {
        Assert.IsTrue(StandaloneToolConsentPolicy.TryParsePermissionMode(
            modeValue,
            out var mode));
        var terminal = new ScriptedTerminal(true);
        using var handler = CreateHandler(terminal, mode);

        var response = await handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: false),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, response.Decision);
        Assert.AreEqual(0, terminal.ReadCount);
        Assert.IsNotNull(response.Grant);
        Assert.IsNotNull(response.Grant.AllowedCallIds);
        Assert.HasCount(1, response.Grant.AllowedCallIds);
    }

    [TestMethod]
    public async Task RequestPermissionAsync_AutoAllStillDeniesInvalidScopeWithoutPrompt()
    {
        var terminal = new ScriptedTerminal(true);
        using var handler = CreateHandler(
            terminal,
            StandaloneToolPermissionMode.AutoAll);
        var context = CreatePermissionContext(canGrantForRun: true) with
        {
            ScopeResolution = ToolConsentScopeResolution.Failure("invalid scope")
        };

        var response = await handler.RequestPermissionAsync(
            context,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Denied, response.Decision);
        Assert.AreEqual(0, terminal.ReadCount);
    }

    [TestMethod]
    public async Task RequestPermissionAsync_AutoAllWorksWithoutInteractiveTerminal()
    {
        var terminal = new ScriptedTerminal(isInteractive: false);
        using var handler = CreateHandler(
            terminal,
            StandaloneToolPermissionMode.AutoAll);

        var response = await handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: false),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, response.Decision);
        Assert.AreEqual(0, terminal.ReadCount);
        Assert.IsNotNull(response.Grant);
    }

    [TestMethod]
    public async Task RequestApprovalAsync_AutoToolsSkipsToolApprovalPromptButNotWorkflow()
    {
        var toolTerminal = new ScriptedTerminal(true);
        using var toolHandler = CreateHandler(
            toolTerminal,
            StandaloneToolPermissionMode.AutoTools);
        var toolResponse = await toolHandler.RequestApprovalAsync(
            CreateApprovalContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, toolResponse.Decision);
        Assert.AreEqual(0, toolTerminal.ReadCount);
        Assert.IsNotNull(toolResponse.Grant);

        var workflowTerminal = new ScriptedTerminal(true, "Y");
        using var workflowHandler = CreateHandler(
            workflowTerminal,
            StandaloneToolPermissionMode.AutoTools);
        var workflowResponse = await workflowHandler.RequestApprovalAsync(
            CreateWorkflowApprovalContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, workflowResponse.Decision);
        Assert.AreEqual(1, workflowTerminal.ReadCount);
        Assert.IsNull(workflowResponse.Grant);
    }

    [TestMethod]
    public async Task RequestApprovalAsync_AutoAllSkipsWorkflowPrompt()
    {
        var terminal = new ScriptedTerminal(isInteractive: false);
        using var handler = CreateHandler(
            terminal,
            StandaloneToolPermissionMode.AutoAll);

        var response = await handler.RequestApprovalAsync(
            CreateWorkflowApprovalContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, response.Decision);
        Assert.AreEqual(0, terminal.ReadCount);
        Assert.IsNull(response.Grant);
    }

    [TestMethod]
    public async Task SetPermissionMode_AskAllRestoresInteractivePrompt()
    {
        var terminal = new ScriptedTerminal(true, "3");
        using var handler = CreateHandler(
            terminal,
            StandaloneToolPermissionMode.AutoAll);
        handler.SetPermissionMode(StandaloneToolPermissionMode.AskAll);

        var response = await handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: true),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Denied, response.Decision);
        Assert.AreEqual(1, terminal.ReadCount);
        Assert.Contains("Tool permission", terminal.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("1", true, true)]
    [DataRow("2", true, false)]
    [DataRow("3", false, false)]
    public async Task RequestPermissionAsync_ParsesOnlyNumberedChoices(
        string input,
        bool expectedGranted,
        bool expectedCallBound)
    {
        var terminal = new ScriptedTerminal(true, input);
        using var handler = CreateHandler(terminal);
        var context = CreatePermissionContext(canGrantForRun: true);

        var response = await handler.RequestPermissionAsync(
            context,
            TestContext.CancellationToken);

        Assert.AreEqual(
            expectedGranted ? ToolAuthorizationDecision.Granted : ToolAuthorizationDecision.Denied,
            response.Decision);
        if (!expectedGranted)
        {
            Assert.IsNull(response.Grant);
            return;
        }

        Assert.IsNotNull(response.Grant);
        Assert.AreEqual(expectedCallBound, response.Grant.AllowedCallIds is not null);
    }

    [TestMethod]
    public async Task RequestPermissionAsync_RunUnavailableRetriesThenDenies()
    {
        var terminal = new ScriptedTerminal(true, "2", "2", "2");
        using var handler = CreateHandler(terminal);

        var response = await handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: false),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Denied, response.Decision);
        Assert.AreEqual(3, terminal.ReadCount);
        Assert.AreEqual(3, Count(terminal.Output, "Select [1/3]:"));
        Assert.AreEqual(
            3,
            Count(
                terminal.Output,
                "Option 2 is unavailable for this tool request. Enter 1 or 3."));
        Assert.DoesNotContain("Select [1/2/3]:", terminal.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("1", true)]
    [DataRow("2", false)]
    public async Task RequestPermissionAsync_DeleteChoicesExecuteWithExpectedLifetime(
        string input,
        bool expectedCallBound)
    {
        var firstPath = Path.Combine(_workspace, "logs", "001.log");
        var secondPath = Path.Combine(_workspace, "logs", "002.log");
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        await File.WriteAllTextAsync(firstPath, "first", TestContext.CancellationToken);
        await File.WriteAllTextAsync(secondPath, "second", TestContext.CancellationToken);
        var firstContext = CreateDeletePermissionContext("call-delete-1", "logs/001.log");
        var secondContext = CreateDeletePermissionContext("call-delete-2", "logs/002.log");
        var terminal = new ScriptedTerminal(true, input);
        using var handler = new StandaloneToolConsentHandler(
            terminal,
            new StandaloneToolGrantFactory());

        var response = await handler.RequestPermissionAsync(
            firstContext,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, response.Decision);
        Assert.IsNotNull(response.Grant);
        Assert.AreEqual(expectedCallBound, response.Grant.AllowedCallIds is not null);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var authorization = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        await authorization.SaveGrantAsync(
            response.Grant,
            ct: TestContext.CancellationToken);

        var firstAuthorization = await authorization.AuthorizeAsync(
            firstContext.Invocation,
            firstContext.Descriptor,
            ct: TestContext.CancellationToken);
        Assert.AreEqual(ToolAuthorizationDecision.Granted, firstAuthorization.Decision);
        Assert.IsNotNull(firstAuthorization.EffectivePermission);
        var executor = new BuiltinFsToolExecutor();
        var firstResult = await executor.ExecuteAsync(
            firstContext.Invocation with
            {
                PermissionContext = firstAuthorization.EffectivePermission
            },
            TestContext.CancellationToken);
        Assert.IsTrue(firstResult.Success, firstResult.Error);
        Assert.IsFalse(File.Exists(firstPath));

        var secondAuthorization = await authorization.AuthorizeAsync(
            secondContext.Invocation,
            secondContext.Descriptor,
            ct: TestContext.CancellationToken);
        Assert.AreEqual(
            expectedCallBound
                ? ToolAuthorizationDecision.Denied
                : ToolAuthorizationDecision.Granted,
            secondAuthorization.Decision);
        if (expectedCallBound)
        {
            Assert.IsTrue(File.Exists(secondPath));
            return;
        }

        Assert.IsNotNull(secondAuthorization.EffectivePermission);
        var secondResult = await executor.ExecuteAsync(
            secondContext.Invocation with
            {
                PermissionContext = secondAuthorization.EffectivePermission
            },
            TestContext.CancellationToken);
        Assert.IsTrue(secondResult.Success, secondResult.Error);
        Assert.IsFalse(File.Exists(secondPath));
        Assert.Contains(
            "2  Allow workspace deletes for this Run",
            terminal.Output,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RequestPermissionAsync_EofAndEmptyInputDefaultToDenial()
    {
        var eofTerminal = new ScriptedTerminal(true, (string?)null);
        var emptyTerminal = new ScriptedTerminal(true, "   ");
        using var eofHandler = CreateHandler(eofTerminal);
        using var emptyHandler = CreateHandler(emptyTerminal);

        var eof = await eofHandler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: true),
            TestContext.CancellationToken);
        var empty = await emptyHandler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: true),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Denied, eof.Decision);
        Assert.AreEqual(ToolAuthorizationDecision.Denied, empty.Decision);
        Assert.AreEqual(1, eofTerminal.ReadCount);
        Assert.AreEqual(1, emptyTerminal.ReadCount);
    }

    [TestMethod]
    [DataRow("Y", ToolAuthorizationDecision.Granted)]
    [DataRow("y", ToolAuthorizationDecision.Granted)]
    [DataRow("N", ToolAuthorizationDecision.Denied)]
    [DataRow("", ToolAuthorizationDecision.Denied)]
    public async Task RequestApprovalAsync_ParsesYesNoAndDefaultsToDenial(
        string input,
        ToolAuthorizationDecision expected)
    {
        var terminal = new ScriptedTerminal(true, input);
        using var handler = CreateHandler(terminal);

        var response = await handler.RequestApprovalAsync(
            CreateApprovalContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(expected, response.Decision);
        Assert.AreEqual(
            expected is ToolAuthorizationDecision.Granted,
            response.Grant is not null);
    }

    [TestMethod]
    public async Task RequestApprovalAsync_InvalidInputStopsAfterThreeAttempts()
    {
        var terminal = new ScriptedTerminal(true, "yes", "1", "maybe");
        using var handler = CreateHandler(terminal);

        var response = await handler.RequestApprovalAsync(
            CreateApprovalContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Denied, response.Decision);
        Assert.AreEqual(3, terminal.ReadCount);
        Assert.AreEqual(3, Count(terminal.Output, "Enter Y or N."));
    }

    [TestMethod]
    public async Task RequestPermissionAsync_CancellationPropagatesAndReleasesPromptGate()
    {
        using var cancellation = new CancellationTokenSource();
        var terminal = new ScriptedTerminal(
            true,
            ct =>
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<string?>(ct);
            },
            _ => ValueTask.FromResult<string?>("3"));
        using var handler = CreateHandler(terminal);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            async () => await handler.RequestPermissionAsync(
                CreatePermissionContext(canGrantForRun: true),
                cancellation.Token));

        var second = await handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: true, callId: "call-2"),
            TestContext.CancellationToken);
        Assert.AreEqual(ToolAuthorizationDecision.Denied, second.Decision);
    }

    [TestMethod]
    public async Task RequestPermissionAsync_OutputContainsOnlySafeSummaryAndHash()
    {
        const string secret = "super-secret-prompt-token";
        var terminal = new ScriptedTerminal(true, "3");
        using var handler = CreateHandler(terminal);
        var context = CreatePermissionContext(
            canGrantForRun: true,
            argumentsJson: $$"""{"path":"safe.txt","content":"{{secret}}"}""");

        _ = await handler.RequestPermissionAsync(
            context,
            TestContext.CancellationToken);

        Assert.Contains("Target: workspace:safe.txt", terminal.Output, StringComparison.Ordinal);
        Assert.Contains(
            $"Arguments SHA-256: {context.Request.ArgumentsHash}",
            terminal.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(secret, terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Invocation.ArgumentsJson, terminal.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RequestPermissionAsync_ConcurrentPromptsAreSerialized()
    {
        var firstRead = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new ScriptedTerminal(
            true,
            async ct =>
            {
                firstReadStarted.TrySetResult();
                return await firstRead.Task.WaitAsync(ct);
            },
            _ => ValueTask.FromResult<string?>("3"));
        using var handler = CreateHandler(terminal);

        var first = handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: true, callId: "call-1"),
            TestContext.CancellationToken);
        await firstReadStarted.Task.WaitAsync(TestContext.CancellationToken);
        var second = handler.RequestPermissionAsync(
            CreatePermissionContext(canGrantForRun: true, callId: "call-2"),
            TestContext.CancellationToken);

        await Task.Yield();
        Assert.AreEqual(1, Count(terminal.Output, "Tool permission"));
        Assert.DoesNotContain("Call: call-2", terminal.Output, StringComparison.Ordinal);

        firstRead.SetResult("3");
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, Count(terminal.Output, "Tool permission"));
        var firstIndex = terminal.Output.IndexOf("Call: call-1", StringComparison.Ordinal);
        var secondIndex = terminal.Output.IndexOf("Call: call-2", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, firstIndex);
        Assert.IsGreaterThan(firstIndex, secondIndex);
    }

    private static StandaloneToolConsentHandler CreateHandler(
        ICliTerminal terminal,
        StandaloneToolPermissionMode mode = StandaloneToolPermissionMode.AskAll) =>
        new(
            terminal,
            new StandaloneToolGrantFactory(new FixedTimeProvider(FixedNow)),
            mode);

    private ToolPermissionConsentContext CreatePermissionContext(
        bool canGrantForRun,
        string callId = "call-1",
        string argumentsJson = "{\"path\":\"safe.txt\"}")
    {
        var invocation = CreateInvocation(callId, argumentsJson);
        var descriptor = CreateDescriptor();
        var argumentsHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(argumentsJson)))
            .ToLowerInvariant();
        return new ToolPermissionConsentContext(
            new ToolPermissionRequest(
                $"permission-{callId}",
                invocation.AgentId,
                invocation.ParentAgentId,
                invocation.ToolId,
                invocation.CallId,
                argumentsHash,
                descriptor.Risk.ToString(),
                canGrantForRun ? "call-or-run" : "call",
                "Permission required.",
                CorrelationId: $"correlation-{callId}",
                RunId: invocation.RunId),
            invocation,
            descriptor,
            ToolConsentScopeResolution.Success(
                new ToolRequestedScope(
                    "workspace:safe.txt",
                    "file read",
                    [Path.Combine(_workspace, "safe.txt")],
                    [],
                    AllowOverwrite: false,
                    AllowMove: false,
                    AllowDelete: false,
                    AllowedExecutables: [],
                    AllowedEnvironmentVariables: [],
                    AllowPowerShell: false,
                    new NetworkPolicy(true, []),
                    canGrantForRun)),
            _workspace);
    }

    private ToolPermissionConsentContext CreateDeletePermissionContext(
        string callId,
        string path)
    {
        var descriptor = new BuiltinToolRegistry()
            .GetTools([BuiltinToolRegistry.FileDeleteToolId])
            .Single();
        var invocation = new ToolInvocation(
            callId,
            descriptor.ToolId,
            "agent-1",
            ParentAgentId: null,
            $"{{\"path\":\"{path.Replace("\\", "/", StringComparison.Ordinal)}\"}}",
            "run-1",
            "session-1",
            $"invocation-{callId}");
        var scopeResolution = new BuiltinToolConsentScopeResolver().Resolve(
            invocation,
            descriptor,
            _workspace);
        Assert.IsTrue(scopeResolution.IsSuccess, scopeResolution.Error);
        var argumentsHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(invocation.ArgumentsJson)))
            .ToLowerInvariant();
        return new ToolPermissionConsentContext(
            new ToolPermissionRequest(
                $"permission-{callId}",
                invocation.AgentId,
                invocation.ParentAgentId,
                invocation.ToolId,
                invocation.CallId,
                argumentsHash,
                descriptor.Risk.ToString(),
                "call-or-run",
                "Permission required.",
                CorrelationId: $"correlation-{callId}",
                RunId: invocation.RunId),
            invocation,
            descriptor,
            scopeResolution,
            _workspace);
    }

    private ToolPermissionConsentContext CreateWritePermissionContext(
        string callId,
        string path)
    {
        var descriptor = new BuiltinToolRegistry()
            .GetTools([BuiltinToolRegistry.FileWriteToolId])
            .Single();
        var invocation = new ToolInvocation(
            callId,
            descriptor.ToolId,
            "agent-1",
            ParentAgentId: null,
            $$"""{"path":"{{path}}","content":"content","encoding":"utf-8","mode":"create"}""",
            "run-1",
            "session-1",
            $"invocation-{callId}");
        var scopeResolution = new BuiltinToolConsentScopeResolver().Resolve(
            invocation,
            descriptor,
            _workspace);
        Assert.IsTrue(scopeResolution.IsSuccess, scopeResolution.Error);
        var argumentsHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(invocation.ArgumentsJson)))
            .ToLowerInvariant();
        return new ToolPermissionConsentContext(
            new ToolPermissionRequest(
                $"permission-{callId}",
                invocation.AgentId,
                invocation.ParentAgentId,
                invocation.ToolId,
                invocation.CallId,
                argumentsHash,
                descriptor.Risk.ToString(),
                "call-or-run",
                "Permission required.",
                CorrelationId: $"correlation-{callId}",
                RunId: invocation.RunId),
            invocation,
            descriptor,
            scopeResolution,
            _workspace);
    }

    private ToolApprovalConsentContext CreateApprovalContext()
    {
        var permission = CreatePermissionContext(canGrantForRun: false);
        return new ToolApprovalConsentContext(
            new ApprovalRequest(
                "approval-correlation",
                "approval-request",
                permission.Invocation.CallId,
                permission.Invocation.RunId,
                permission.Invocation.AgentId,
                permission.Invocation.ParentAgentId,
                permission.Invocation.ToolId,
                permission.Request.ArgumentsHash,
                permission.Descriptor.Risk,
                permission.ScopeResolution.Scope!.TargetSummary,
                "Approval required."),
            permission.Invocation,
            permission.Descriptor,
            permission.ScopeResolution,
            _workspace);
    }

    private ToolApprovalConsentContext CreateWorkflowApprovalContext()
    {
        var approval = CreateApprovalContext();
        return new ToolApprovalConsentContext(
            approval.Request,
            Invocation: null,
            Descriptor: null,
            ToolConsentScopeResolution.Failure("Workflow approval has no tool scope."),
            _workspace);
    }

    private static ToolInvocation CreateInvocation(string callId, string argumentsJson) =>
        new(
            callId,
            "mcli_fs_read",
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            "invocation-1");

    private static ToolDescriptor CreateDescriptor() =>
        new(
            "mcli_fs_read",
            "builtin",
            "Read file",
            "Reads a file.",
            "{}",
            Risk: ToolRiskLevel.SensitiveRead,
            Capabilities: ["filesystem.read"]);

    private static int Count(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptedTerminal : ICliTerminal
    {
        private readonly bool _isInteractive;
        private readonly ConcurrentQueue<
            Func<CancellationToken, ValueTask<string?>>> _reads = new();
        private readonly StringBuilder _output = new();
        private readonly object _outputSync = new();
        private int _readCount;

        public ScriptedTerminal(bool isInteractive)
        {
            _isInteractive = isInteractive;
        }

        public ScriptedTerminal(bool isInteractive, params string?[] reads)
            : this(
                isInteractive,
                reads.Select<string?, Func<CancellationToken, ValueTask<string?>>>(
                    value => _ => ValueTask.FromResult(value)).ToArray())
        {
        }

        public ScriptedTerminal(
            bool isInteractive,
            params Func<CancellationToken, ValueTask<string?>>[] reads)
        {
            _isInteractive = isInteractive;
            foreach (var read in reads)
            {
                _reads.Enqueue(read);
            }
        }

        public bool IsInteractive => _isInteractive;

        public string Output
        {
            get
            {
                lock (_outputSync)
                {
                    return _output.ToString();
                }
            }
        }

        public int ReadCount => Volatile.Read(ref _readCount);

        public void Write(string value) => Write(value, CliTextStyle.Default);

        public void Write(string value, CliTextStyle style)
        {
            _ = style;
            lock (_outputSync)
            {
                _output.Append(value);
            }
        }

        public void WriteLine(string value = "") => WriteLine(value, CliTextStyle.Default);

        public void WriteLine(string value, CliTextStyle style)
        {
            _ = style;
            lock (_outputSync)
            {
                _output.AppendLine(value);
            }
        }

        public void Clear()
        {
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _readCount);
            ct.ThrowIfCancellationRequested();
            return _reads.TryDequeue(out var read)
                ? read(ct)
                : ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string> ReadSecretAsync(
            string prompt,
            CancellationToken ct = default)
        {
            Write(prompt);
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(string.Empty);
        }
    }
}
