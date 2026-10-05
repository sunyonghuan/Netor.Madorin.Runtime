using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

internal static class ReferenceHost
{
    private const string ProviderId = "reference-provider";
    private const string ModelId = "reference-model";
    private const string PermissionToolId = "host.reference.permission";
    private const string ApprovalToolId = "mcp.reference.approval";
    private static readonly object OutputGate = new();

    public static async Task<int> RunAsync(string[] args)
    {
        RuntimeHandle[] handles = [];
        try
        {
            var configuration = await ReferenceHostConfiguration.ReadAsync(
                args,
                Console.In,
                CancellationToken.None);
            using var demoTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            handles = await ConnectAsync(configuration, demoTimeout.Token);
            WriteLine(
                $"READY|reference|{configuration.HostInstanceId}|"
                + string.Join('|', handles.Select(static handle =>
                    handle.Client.InstanceBinding.RuntimeInstanceId)));

            var state = await RunDemoAsync(handles, demoTimeout.Token);
            return await RunCommandLoopAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"reference-host failed: {ex.Message}");
            return 1;
        }
        finally
        {
            foreach (var handle in handles.Reverse())
            {
                await handle.Client.DisposeAsync();
            }
        }
    }

    private static async Task<RuntimeHandle[]> ConnectAsync(
        ReferenceHostConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var handles = configuration.Runtimes
            .Select((endpoint, index) => CreateHandle(
                configuration.HostInstanceId,
                endpoint,
                index))
            .ToArray();
        try
        {
            await Task.WhenAll(handles.Select(handle =>
                ConnectHandleAsync(handle, cancellationToken)));
            foreach (var handle in handles)
            {
                var binding = handle.Client.InstanceBinding;
                if (!string.Equals(
                        binding.RuntimeInstanceId,
                        handle.Endpoint.RuntimeInstanceId,
                        StringComparison.Ordinal)
                    || binding.Workspace is not { } boundWorkspace
                    || !string.Equals(
                        Path.GetFullPath(boundWorkspace),
                        handle.Endpoint.Workspace,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Runtime {handle.Index} authenticated with an unexpected binding.");
                }
            }

            return handles;
        }
        catch
        {
            foreach (var handle in handles.Reverse())
            {
                await handle.Client.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task ConnectHandleAsync(
        RuntimeHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            await handle.Client.ConnectAsync(cancellationToken);
        }
        catch (RuntimeClientConnectionException ex) when (ex.IsRetryable)
        {
            await handle.Client.ReconnectAsync(cancellationToken);
        }
    }

    private static RuntimeHandle CreateHandle(
        string hostInstanceId,
        ReferenceHostConfiguration.RuntimeEndpoint endpoint,
        int index)
    {
        var callbacks = new ReferenceHostCallbacks(endpoint.Workspace);
        var supplementalSkillsDirectory = Path.Combine(
            endpoint.Workspace,
            ".madorin",
            "host-skills");
        SkillSourceDescriptor[] skillSources = Directory.Exists(supplementalSkillsDirectory)
            ? [new($"reference-host-{index}", SkillSourceKind.Directory, supplementalSkillsDirectory)]
            : [];
        var client = new RuntimeClient(
            new RuntimeClientOptions(
                endpoint.PipeName,
                hostInstanceId,
                ExpectedRuntimeInstanceId: endpoint.RuntimeInstanceId,
                HandshakeSecret: endpoint.Secret,
                WorkspaceDirectory: endpoint.Workspace,
                ReconnectWindow: TimeSpan.FromSeconds(30),
                Capabilities: new RuntimeCapabilities(
                    ReverseRpc: true,
                    ToolCatalog: true,
                    ToolPermissions: true,
                    Skills: new SkillsCapabilities(
                        SourceKinds: ["directory"],
                        ResourcesSupported: true,
                        ScriptsSupported: true,
                        HotRefreshSupported: true)),
                HostCallbacks: callbacks.CreateConfiguration(),
                SkillSources: skillSources));
        return new RuntimeHandle(
            index,
            endpoint,
            client,
            callbacks,
            CreateSelection(index, selectionVersion: 1));
    }

    private static async Task<DemoState> RunDemoAsync(
        RuntimeHandle[] handles,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(handles.Select(handle =>
            InspectSkillsAsync(handle, cancellationToken)));
        await Task.WhenAll(handles.Select(handle => handle.Client
            .ReplaceToolCatalogAsync(CreateToolCatalog(), cancellationToken)
            .AsTask()));

        var runIds = await Task.WhenAll(handles.Select(handle => handle.Client
            .StartNewSessionRunAsync(
                CreateRunRequest(handle.Index, handle.InitialSelection),
                cancellationToken)
            .AsTask()));
        var captures = await Task.WhenAll(handles.Select((handle, index) =>
            CaptureRunAsync(
                handle,
                runIds[index],
                eventReplayCursor: 0,
                cancellationToken)));
        foreach (var capture in captures.Where(static capture => capture.AwaitingConfirmation))
        {
            WriteLine(
                $"WAITING|{capture.RunId}|{capture.LastGsn.ToString(System.Globalization.CultureInfo.InvariantCulture)}|"
                + (capture.FailureMessage ?? "connection requires confirmation"));
        }

        var failedCapture = captures.FirstOrDefault(static capture =>
            !capture.AwaitingConfirmation
            && capture.Terminal?.MessageType != MessageTypes.RunCompleted);
        if (failedCapture is not null)
        {
            throw new InvalidOperationException(
                $"Reference Run '{failedCapture.RunId}' ended with "
                + $"'{failedCapture.Terminal?.MessageType}': "
                + (failedCapture.Terminal?.Payload.GetRawText() ?? "no terminal event"));
        }

        var expert = handles[0];
        var updatedSelection = expert.LastSnapshot is not null
            && !captures[0].AwaitingConfirmation
            ? CreateExpertSelection(
                selectionVersion: 2,
                "Selection updated by the reference host.")
            : expert.InitialSelection;
        RunCapture cancelled;
        if (!captures[0].AwaitingConfirmation)
        {
            var persistedSelection = await expert.Client.UpdateSessionSelectionAsync(
                new SessionSelectionUpdateParameters(
                    captures[0].SessionId,
                    ExpectedSelectionVersion: 1,
                    updatedSelection),
                cancellationToken);
            if (persistedSelection.SelectionVersion != updatedSelection.SelectionVersion)
            {
                throw new InvalidDataException(
                    "The Runtime did not apply the reference host Selection update.");
            }

            var queriedResult = await expert.Callbacks.QueryKnownResultAsync(
                "reference-permission-call",
                cancellationToken);
            if (queriedResult.Status is not ToolCallStatus.Succeeded)
            {
                throw new InvalidDataException(
                    "The reference host could not query its durable tool result by callId.");
            }

            var cancelRunId = await expert.Client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    $"reference-cancel-session-{Guid.NewGuid():N}",
                    $"reference-cancel-run-{Guid.NewGuid():N}",
                    RuntimeMode.Expert,
                    expert.InitialSelection,
                    [new TextContentBlock("Cancel the reference run.")]),
                cancellationToken);
            if (!await expert.Client.CancelRunAsync(
                    cancelRunId,
                    "reference-host cancellation demonstration",
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Runtime did not accept cancellation for Run '{cancelRunId}'.");
            }

            cancelled = await CaptureRunAsync(
                expert,
                cancelRunId,
                captures[0].LastGsn,
                cancellationToken);
        }
        else
        {
            cancelled = captures[0];
        }
        var currentSelections = handles.Select(static handle => handle.InitialSelection).ToArray();
        if (!captures[0].AwaitingConfirmation)
        {
            currentSelections[0] = updatedSelection;
        }
        var state = new DemoState(handles, captures, currentSelections, cancelled);
        WriteDemo(state);
        return state;
    }

    private static async Task InspectSkillsAsync(
        RuntimeHandle handle,
        CancellationToken cancellationToken)
    {
        var current = await handle.Client.GetSkillCatalogAsync(cancellationToken);
        var refreshed = await handle.Client.RefreshSkillCatalogAsync(cancellationToken);
        WriteLine(
            $"SKILLS|{handle.Endpoint.RuntimeInstanceId}|"
            + $"{current.CatalogVersion}|{refreshed.CatalogVersion}|"
            + $"{refreshed.SkillCount}|{string.Join(',', refreshed.Sources.Select(static source => source.SourceId))}");
    }

    private static async Task<int> RunCommandLoopAsync(
        DemoState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var command = await Console.In.ReadLineAsync(cancellationToken);
            if (command is null || string.Equals(command, "exit", StringComparison.Ordinal))
            {
                WriteLine("OK|exit");
                return 0;
            }

            var parts = command.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is ["recover", var indexText]
                && int.TryParse(
                    indexText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var index)
                && index >= 0
                && index < state.Handles.Length)
            {
                await RecoverAsync(state, index, cancellationToken);
                continue;
            }

            WriteLine($"ERROR|unknown-command|{command}");
        }
    }

    private static async Task RecoverAsync(
        DemoState state,
        int index,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var handle = state.Handles[index];
        var previous = state.Captures[index];
        var resume = await RetryConnectionAsync(
            handle.Client,
            () => handle.Client.ResumeSessionAsync(previous.SessionId, timeout.Token),
            timeout.Token);
        var eventCursor = Math.Max(state.LastGsns[index], resume.LastGsn);
        var originalRun = await handle.Client.QueryRunAsync(previous.RunId, timeout.Token);
        var sessionRuns = await handle.Client.ListRunsAsync(
            new RunListParameters(previous.SessionId),
            timeout.Token);
        var nonTerminalRuns = sessionRuns.Runs
            .Where(static run => run.Status is not (
                RunStatus.Completed
                or RunStatus.Failed
                or RunStatus.Cancelled
                or RunStatus.Interrupted))
            .ToArray();
        WriteLine(
            $"RECOVER_STATE|{index}|{previous.RunId}|{originalRun.Status}|"
            + $"{nonTerminalRuns.Length}|{eventCursor.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        var rehydrated = await handle.Client.RehydrateSessionAsync(
            new SessionRehydrateParameters(
                previous.SessionId,
                GetAgentDefinitions(state.CurrentSelections[index])),
            timeout.Token);
        var runId = await handle.Client.StartExistingSessionRunAsync(
            new ExistingSessionRunRequest(
                previous.SessionId,
                $"reference-recovery-{index}-{Guid.NewGuid():N}",
                InputOverride:
                [
                    new TextContentBlock(index == 0
                        ? "continue after restart"
                        : "continue reference session")
                ],
                ExpectedSelectionVersion: resume.LatestSelectionVersion),
            timeout.Token);
        var terminal = await CaptureRunAsync(
            handle,
            runId,
            eventCursor,
            timeout.Token);
        state.LastGsns[index] = terminal.LastGsn;
        WriteLine(
            $"OK|recover|{index}|{previous.SessionId}|{rehydrated.Status}|"
            + $"{rehydrated.Mismatched.Length}|{runId}|{GetTerminalType(terminal)}|"
            + terminal.LastGsn.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async ValueTask<T> RetryConnectionAsync<T>(
        RuntimeClient client,
        Func<ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (RuntimeClientConnectionException ex) when (ex.IsRetryable)
            {
                await client.ReconnectAsync(cancellationToken);
            }
        }
    }

    private static async Task<RunCapture> CaptureRunAsync(
        RuntimeHandle handle,
        string runId,
        long eventReplayCursor,
        CancellationToken cancellationToken)
    {
        var lastGsn = eventReplayCursor;
        long lastRunSequence = -1;
        try
        {
            await foreach (var envelope in handle.Client.ReadEventsAsync(
                eventReplayCursor,
                cancellationToken))
            {
                if (!string.Equals(
                    envelope.RuntimeInstanceId,
                    handle.Endpoint.RuntimeInstanceId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Runtime {handle.Index} received an event from another instance.");
                }

                if (envelope.Gsn <= lastGsn)
                {
                    throw new InvalidDataException(
                        $"Runtime {handle.Index} emitted non-monotonic GSN {envelope.Gsn}.");
                }

                if (string.Equals(envelope.RunId, runId, StringComparison.Ordinal))
                {
                    if (envelope.RunSequence <= lastRunSequence)
                    {
                        throw new InvalidDataException(
                            $"Run '{runId}' emitted non-monotonic sequence {envelope.RunSequence}.");
                    }

                    lastRunSequence = envelope.RunSequence;
                }

                WriteLine(
                    $"EVENT|{handle.Index}|{envelope.RuntimeInstanceId}|{envelope.RunId}|"
                    + $"{envelope.Gsn.ToString(System.Globalization.CultureInfo.InvariantCulture)}|"
                    + $"{envelope.RunSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}|"
                    + envelope.MessageType);
                await handle.Client.AcknowledgeEventsAsync(envelope.Gsn, cancellationToken);
                lastGsn = envelope.Gsn;
                if (string.Equals(envelope.RunId, runId, StringComparison.Ordinal)
                    && envelope.MessageType is MessageTypes.RunCompleted
                        or MessageTypes.RunFailed
                        or MessageTypes.RunCancelled)
                {
                    var run = await handle.Client.QueryRunAsync(runId, cancellationToken);
                    var capture = new RunCapture(
                        run.SessionId,
                        runId,
                        envelope,
                        lastGsn,
                        run,
                        AwaitingConfirmation: false,
                        FailureMessage: null);
                    handle.RecordSnapshot(capture);
                    return capture;
                }
            }

            throw new EndOfStreamException($"Run '{runId}' ended without a terminal event.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RuntimeClientConnectionException
            or IOException
            or EndOfStreamException
            or InvalidDataException
            or TimeoutException
            or ObjectDisposedException)
        {
            handle.MarkAwaitingConfirmation(runId, lastGsn, ex);
            return new RunCapture(
                handle.LastSnapshot?.SessionId ?? string.Empty,
                runId,
                Terminal: null,
                LastGsn: lastGsn,
                Snapshot: handle.LastSnapshot,
                AwaitingConfirmation: true,
                FailureMessage: ex.Message);
        }
    }

    private static NewSessionRunRequest CreateRunRequest(
        int index,
        NextTurnSelection selection) => new(
            $"reference-session-{index}-{Guid.NewGuid():N}",
            $"reference-run-{index}-{Guid.NewGuid():N}",
            selection.Mode,
            selection,
            [new TextContentBlock($"Run reference {selection.Mode} mode.")]);

    private static NextTurnSelection CreateSelection(
        int index,
        int selectionVersion) => index switch
        {
            0 => CreateExpertSelection(
                selectionVersion,
                "Use both reference host tools before answering."),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, null)
        };

    private static NextTurnSelection CreateExpertSelection(
        int selectionVersion,
        string systemPrompt) => new(
            selectionVersion,
            RuntimeMode.Expert,
            new DefaultSelection(ProviderId, ModelId),
            new ExpertModeOptions(
                new AgentRef(
                    "reference-expert",
                    "v1",
                    systemPrompt,
                    ProviderId,
                    ModelId,
                    [PermissionToolId, ApprovalToolId])),
            ToolCatalogVersion: "reference-v1");

    private static AgentDefinition[] GetAgentDefinitions(NextTurnSelection selection) =>
        selection.ModeOptions switch
        {
            ExpertModeOptions expert => [new AgentDefinition(expert.Agent)],
            _ => throw new InvalidDataException(
                $"Unsupported reference-host mode options '{selection.ModeOptions.GetType().Name}'.")
        };

    private static ToolCatalogReplaceRequest CreateToolCatalog()
    {
        var inputSchema = ParseElement(
            """{"type":"object","additionalProperties":true}""");
        var outputSchema = ParseElement(
            """{"type":"object","additionalProperties":true}""");
        return new ToolCatalogReplaceRequest(
            "reference-v1",
            [
                new ToolCatalogItem(
                    PermissionToolId,
                    "Reference Permission Tool",
                    "Demonstrates a host Grant callback.",
                    inputSchema,
                    outputSchema,
                    ToolRiskLevel.Low,
                    5,
                    ["reference"],
                    [],
                    ToolExecutionTarget.Host,
                    RequiresApproval: false,
                    IsIdempotent: true),
                new ToolCatalogItem(
                    ApprovalToolId,
                    "Reference Approval Tool",
                    "Demonstrates an explicit approval callback.",
                    inputSchema,
                    outputSchema,
                    ToolRiskLevel.Low,
                    5,
                    ["reference", "mcp"],
                    [],
                    ToolExecutionTarget.Mcp,
                    RequiresApproval: true,
                    IsIdempotent: true)
            ]);
    }

    private static void WriteDemo(DemoState state)
    {
        var expertCallbacks = state.Handles[0].Callbacks;
        WriteLine(
            $"DEMO|{state.Captures[0].SessionId}|{state.Captures[1].SessionId}|"
            + $"{state.Captures[2].SessionId}|{state.Captures[0].RunId}|"
            + $"{state.Captures[1].RunId}|{state.Captures[2].RunId}|"
            + $"{state.Cancelled.RunId}|{GetTerminalType(state.Captures[0])}|"
            + $"{GetTerminalType(state.Captures[1])}|{GetTerminalType(state.Captures[2])}|"
            + $"{GetTerminalType(state.Cancelled)}|{state.Captures[0].LastGsn}|"
            + $"{state.Captures[1].LastGsn}|{state.Captures[2].LastGsn}|"
            + $"{state.Cancelled.LastGsn}|{state.CurrentSelections[0].SelectionVersion}|"
            + $"{expertCallbacks.PermissionCount}|{expertCallbacks.ApprovalCount}|"
            + $"{expertCallbacks.ToolCallCount}|{expertCallbacks.ToolQueryCount}");
    }

    private static void WriteLine(string value)
    {
        lock (OutputGate)
        {
            Console.WriteLine(value);
        }
    }

    private static string GetTerminalType(RunCapture capture) =>
        capture.Terminal?.MessageType ?? "awaiting-confirmation";

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record RuntimeHandle(
        int Index,
        ReferenceHostConfiguration.RuntimeEndpoint Endpoint,
        RuntimeClient Client,
        ReferenceHostCallbacks Callbacks,
        NextTurnSelection InitialSelection)
    {
        private readonly object _stateGate = new();

        public RunQueryResult? LastSnapshot { get; private set; }

        public bool AwaitingConfirmation { get; private set; }

        public string? LastFailureMessage { get; private set; }

        public void RecordSnapshot(RunCapture capture)
        {
            lock (_stateGate)
            {
                LastSnapshot = capture.Snapshot;
                AwaitingConfirmation = false;
                LastFailureMessage = null;
            }
        }

        public void MarkAwaitingConfirmation(string runId, long lastGsn, Exception exception)
        {
            lock (_stateGate)
            {
                AwaitingConfirmation = true;
                LastFailureMessage = exception.Message;
            }
        }
    }

    private sealed record RunCapture(
        string SessionId,
        string RunId,
        RuntimeEventEnvelope? Terminal,
        long LastGsn,
        RunQueryResult? Snapshot,
        bool AwaitingConfirmation,
        string? FailureMessage);

    private sealed class DemoState(
        RuntimeHandle[] handles,
        RunCapture[] captures,
        NextTurnSelection[] currentSelections,
        RunCapture cancelled)
    {
        public RuntimeHandle[] Handles { get; } = handles;

        public RunCapture[] Captures { get; } = captures;

        public NextTurnSelection[] CurrentSelections { get; } = currentSelections;

        public RunCapture Cancelled { get; } = cancelled;

        public long[] LastGsns { get; } =
            [cancelled.LastGsn, captures[1].LastGsn, captures[2].LastGsn];
    }
}
