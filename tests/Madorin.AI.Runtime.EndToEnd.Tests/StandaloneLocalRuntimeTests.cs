using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Transport.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class StandaloneLocalRuntimeTests
{
    private static readonly DateTimeOffset FixedLogUtcNow =
        new(2026, 7, 29, 12, 16, 30, TimeSpan.Zero);

    private static readonly TimeZoneInfo FixedLogTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "MadorinTestChinaStandardTime",
        TimeSpan.FromHours(8),
        "Madorin test China Standard Time",
        "Madorin test China Standard Time");

    private static readonly string[] ExpectedRuntimeLifecycleEvents =
    [
        "RuntimeStarting",
        "RuntimeStarted",
        "RuntimeStopping",
        "RuntimeStopped"
    ];

    private readonly TestContext _testContext;

    public StandaloneLocalRuntimeTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void CreateContext_NormalizesWorkspaceAndDerivesStableWorkspaceKey()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);

            var first = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            var second = StandaloneRuntimeContext.Create(
                Path.Combine(workspace, "."),
                configDirectory: configDirectory);
            var overridden = StandaloneRuntimeContext.Create(
                workspace,
                Path.Combine(root, "custom-data"),
                configDirectory);

            Assert.AreEqual(first.WorkspaceRoot, second.WorkspaceRoot);
            Assert.AreEqual(first.WorkspaceKey, second.WorkspaceKey);
            Assert.AreEqual(64, first.WorkspaceKey.Length);
            Assert.AreNotEqual(first.RuntimeInstanceId, second.RuntimeInstanceId);
            Assert.AreEqual(
                Path.Combine(workspace, ".madorin"),
                first.DataDirectory);
            Assert.IsTrue(first.UsesDefaultDataDirectory);
            Assert.AreEqual(
                Path.Combine(configDirectory, "data", "workspaces", first.WorkspaceKey),
                first.LegacyDataDirectory);
            Assert.AreEqual(first.WorkspaceKey, overridden.WorkspaceKey);
            Assert.AreEqual(Path.Combine(root, "custom-data"), overridden.DataDirectory);
            Assert.IsFalse(overridden.UsesDefaultDataDirectory);
            Assert.AreEqual(
                Path.Combine(overridden.DataDirectory, "logs"),
                overridden.LogDirectory);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task CreatePreparedContext_MigratesLegacyDataAndPreservesWorkspaceFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace with 空格");
            var configDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(workspace);
            var context = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            Assert.IsNotNull(context.LegacyDataDirectory);
            Directory.CreateDirectory(Path.Combine(context.LegacyDataDirectory, "messages"));
            await File.WriteAllTextAsync(
                Path.Combine(context.LegacyDataDirectory, "state.db"),
                "legacy-database",
                _testContext.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(context.LegacyDataDirectory, "messages", "session.jsonl"),
                "{\"sequence\":1}",
                _testContext.CancellationToken);
            Directory.CreateDirectory(context.DataDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(context.DataDirectory, "config.json"),
                "{\"defaultModel\":\"workspace-model\"}",
                _testContext.CancellationToken);

            var prepared = await StandaloneRuntimeContext.CreatePreparedAsync(
                workspace,
                configDirectory: configDirectory,
                ct: _testContext.CancellationToken);

            Assert.AreEqual(context.DataDirectory, prepared.DataDirectory);
            Assert.AreEqual(
                "legacy-database",
                await File.ReadAllTextAsync(
                    Path.Combine(prepared.DataDirectory, "state.db"),
                    _testContext.CancellationToken));
            Assert.AreEqual(
                "{\"defaultModel\":\"workspace-model\"}",
                await File.ReadAllTextAsync(
                    Path.Combine(prepared.DataDirectory, "config.json"),
                    _testContext.CancellationToken));
            Assert.IsTrue(File.Exists(Path.Combine(
                prepared.DataDirectory,
                StandaloneWorkspaceDataMigrator.MigrationMarkerFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(context.LegacyDataDirectory, "state.db")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task StartAsync_WritesJsonLifecycleLogIntoDateDirectoryAndMinuteFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var runtimeInstanceId = Guid.NewGuid().ToString("N");
            var logDirectory = Path.Combine(dataDirectory, "logs");
            var logPath = Path.Combine(logDirectory, "20260729", "runtime.jsonl");
            var options = new LocalRuntimeOptions(
                workspace,
                dataDirectory,
                logDirectory,
                runtimeInstanceId,
                _ => new RecordingProvider())
            {
                TimeProvider = new FixedTimeProvider(FixedLogUtcNow, FixedLogTimeZone)
            };

            await using (var runtime = await LocalRuntime.StartAsync(
                options,
                _testContext.CancellationToken))
            {
                var activeLog = new FileInfo(logPath);
                Assert.IsTrue(activeLog.Exists);
                Assert.IsGreaterThan(0, activeLog.Length);
            }

            var lines = await File.ReadAllLinesAsync(logPath, _testContext.CancellationToken);
            Assert.HasCount(4, lines);
            var events = lines.Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                Assert.AreEqual(runtimeInstanceId, document.RootElement
                    .GetProperty("runtimeInstanceId")
                    .GetString());
                Assert.AreEqual(Path.GetFullPath(workspace), document.RootElement
                    .GetProperty("workspace")
                    .GetString());
                return document.RootElement.GetProperty("event").GetString();
            }).ToArray();
            CollectionAssert.AreEqual(
                ExpectedRuntimeLifecycleEvents,
                events);
            Assert.IsFalse(Directory.Exists(Path.Combine(logDirectory, runtimeInstanceId)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task WriteAsync_WritesRuntimeErrorAndExceptionDetails()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            var runtimeError = new RuntimeError(
                "ProviderRejected",
                "provider",
                "The Provider rejected the test request.",
                IsRetryable: true,
                ProviderDetails: null,
                "diagnostic-1");
            Exception? exception = null;
            try
            {
                throw new InvalidOperationException("Provider transport failed.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            await using (var log = RuntimeDiagnosticLog.Create(
                logDirectory,
                "runtime-1",
                root,
                RuntimeDiagnosticLogLevel.Debug,
                new FixedTimeProvider(FixedLogUtcNow, FixedLogTimeZone)))
            {
                await log.WriteAsync(
                    RuntimeDiagnosticLogLevel.Error,
                    "RunFailed",
                    "run-1",
                    "session-1",
                    exception: exception,
                    error: runtimeError,
                    ct: _testContext.CancellationToken);
            }

            var logPath = Path.Combine(logDirectory, "20260729", "runtime.jsonl");
            var lines = await File.ReadAllLinesAsync(logPath, _testContext.CancellationToken);
            var line = Assert.ContainsSingle(lines);
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            Assert.AreEqual("ProviderRejected", entry.GetProperty("errorCode").GetString());
            Assert.AreEqual("provider", entry.GetProperty("errorCategory").GetString());
            Assert.AreEqual(
                "The Provider rejected the test request.",
                entry.GetProperty("message").GetString());
            Assert.IsTrue(entry.GetProperty("isRetryable").GetBoolean());
            Assert.AreEqual("diagnostic-1", entry.GetProperty("diagnosticId").GetString());
            Assert.AreEqual(
                typeof(InvalidOperationException).FullName,
                entry.GetProperty("exceptionType").GetString());
            Assert.AreEqual(
                "Provider transport failed.",
                entry.GetProperty("exceptionMessage").GetString());
            Assert.Contains(
                nameof(WriteAsync_WritesRuntimeErrorAndExceptionDetails),
                entry.GetProperty("stackTrace").GetString()!,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task RunAsync_FailedProviderWritesRuntimeErrorDetails()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var runtimeError = new RuntimeError(
                "ProviderUnavailable",
                "provider",
                "The test Provider is unavailable.",
                IsRetryable: true,
                ProviderDetails: null,
                "diagnostic-provider-1");
            var options = CreateOptions(
                workspace,
                dataDirectory,
                new FailingProvider(runtimeError)) with
            {
                TimeProvider = new FixedTimeProvider(FixedLogUtcNow, FixedLogTimeZone)
            };

            await using (var runtime = await LocalRuntime.StartAsync(
                options,
                _testContext.CancellationToken))
            {
                var events = await CollectAsync(
                    runtime.RunAsync(
                        new NewSessionRunRequest(
                            "request-failure",
                            "turn-failure",
                            RuntimeMode.Expert,
                            CreateSelection(),
                            [new TextContentBlock("fail")]),
                        _testContext.CancellationToken),
                    _testContext.CancellationToken);
                Assert.IsTrue(events.Any(static envelope =>
                    envelope.MessageType == MessageTypes.RunFailed));
            }

            var logPath = Path.Combine(dataDirectory, "logs", "20260729", "runtime.jsonl");
            var lines = await File.ReadAllLinesAsync(logPath, _testContext.CancellationToken);
            var failedLine = Assert.ContainsSingle(lines.Where(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("event").GetString() == "RunFailed";
            }));
            using var failedDocument = JsonDocument.Parse(failedLine);
            var failedEntry = failedDocument.RootElement;
            Assert.AreEqual("ProviderUnavailable", failedEntry.GetProperty("errorCode").GetString());
            Assert.AreEqual(
                "The test Provider is unavailable.",
                failedEntry.GetProperty("message").GetString());
            Assert.AreEqual(
                "diagnostic-provider-1",
                failedEntry.GetProperty("diagnosticId").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task CreatePreparedContext_WithExplicitDataDirectory_DoesNotMigrateLegacyData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var configDirectory = Path.Combine(root, "config");
            var explicitDataDirectory = Path.Combine(root, "explicit-data");
            Directory.CreateDirectory(workspace);
            var defaultContext = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            Assert.IsNotNull(defaultContext.LegacyDataDirectory);
            Directory.CreateDirectory(defaultContext.LegacyDataDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(defaultContext.LegacyDataDirectory, "state.db"),
                "legacy-database",
                _testContext.CancellationToken);

            var prepared = await StandaloneRuntimeContext.CreatePreparedAsync(
                workspace,
                explicitDataDirectory,
                configDirectory,
                _testContext.CancellationToken);

            Assert.AreEqual(Path.GetFullPath(explicitDataDirectory), prepared.DataDirectory);
            Assert.IsFalse(Directory.Exists(explicitDataDirectory));
            Assert.IsFalse(File.Exists(Path.Combine(explicitDataDirectory, "state.db")));
            Assert.IsTrue(File.Exists(Path.Combine(defaultContext.LegacyDataDirectory, "state.db")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task CreatePreparedContext_WhenBothLocationsContainRuntimeData_RejectsMerge()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var configDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(workspace);
            var context = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            Assert.IsNotNull(context.LegacyDataDirectory);
            Directory.CreateDirectory(context.LegacyDataDirectory);
            Directory.CreateDirectory(context.DataDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(context.LegacyDataDirectory, "state.db"),
                "legacy",
                _testContext.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(context.DataDirectory, "state.db"),
                "local",
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<StandaloneWorkspaceDataMigrationException>(
                () => StandaloneRuntimeContext.CreatePreparedAsync(
                    workspace,
                    configDirectory: configDirectory,
                    ct: _testContext.CancellationToken));

            Assert.Contains("both", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual(
                "legacy",
                await File.ReadAllTextAsync(
                    Path.Combine(context.LegacyDataDirectory, "state.db"),
                    _testContext.CancellationToken));
            Assert.AreEqual(
                "local",
                await File.ReadAllTextAsync(
                    Path.Combine(context.DataDirectory, "state.db"),
                    _testContext.CancellationToken));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task CreatePreparedContext_ConcurrentMigration_IsIdempotent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var configDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(workspace);
            var context = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            Assert.IsNotNull(context.LegacyDataDirectory);
            Directory.CreateDirectory(context.LegacyDataDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(context.LegacyDataDirectory, "state.db"),
                new string('x', 128 * 1024),
                _testContext.CancellationToken);

            var first = StandaloneRuntimeContext.CreatePreparedAsync(
                workspace,
                configDirectory: configDirectory,
                ct: _testContext.CancellationToken);
            var second = StandaloneRuntimeContext.CreatePreparedAsync(
                Path.Combine(workspace, "."),
                configDirectory: configDirectory,
                ct: _testContext.CancellationToken);
            await Task.WhenAll(first, second);

            Assert.IsTrue(File.Exists(Path.Combine(workspace, ".madorin", "state.db")));
            Assert.IsTrue(File.Exists(Path.Combine(
                workspace,
                ".madorin",
                StandaloneWorkspaceDataMigrator.MigrationMarkerFileName)));
            Assert.AreEqual(1, Directory.EnumerateFiles(
                Path.Combine(workspace, ".madorin"),
                StandaloneWorkspaceDataMigrator.MigrationMarkerFileName).Count());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void CreateContext_WithoutWorkspace_UsesCurrentDirectory()
    {
        var root = CreateTemporaryDirectory();
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);

            var context = StandaloneRuntimeContext.Create(
                configDirectory: Path.Combine(root, "config"));

            Assert.AreEqual(Path.TrimEndingDirectorySeparator(root), context.WorkspaceRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task RunAsync_NewThenExistingSession_ReusesLocalRuntimeWithoutIpcArtifacts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var provider = new RecordingProvider();
            await using var runtime = await LocalRuntime.StartAsync(
                CreateOptions(workspace, dataDirectory, provider),
                _testContext.CancellationToken);
            var selection = CreateSelection();

            var firstEvents = await CollectAsync(
                runtime.RunAsync(
                    new NewSessionRunRequest(
                        Guid.NewGuid().ToString("N"),
                        Guid.NewGuid().ToString("N"),
                        RuntimeMode.Expert,
                        selection,
                        [new TextContentBlock("first")]),
                    _testContext.CancellationToken),
                _testContext.CancellationToken);
            var acceptedEnvelope = Assert.ContainsSingle(
                firstEvents.Where(static envelope =>
                    envelope.MessageType == MessageTypes.RunAccepted));
            var accepted = acceptedEnvelope.Payload.Deserialize(
                RuntimeJsonContext.Default.RunAcceptedEvent);
            Assert.IsNotNull(accepted);

            var secondEvents = await CollectAsync(
                runtime.RunAsync(
                    new ExistingSessionRunRequest(
                        accepted.SessionId,
                        Guid.NewGuid().ToString("N"),
                        selection,
                        [new TextContentBlock("second")]),
                    _testContext.CancellationToken),
                _testContext.CancellationToken);

            Assert.IsTrue(firstEvents.Any(
                static envelope => envelope.MessageType == MessageTypes.RunCompleted));
            Assert.IsTrue(secondEvents.Any(
                static envelope => envelope.MessageType == MessageTypes.RunCompleted));
            Assert.HasCount(2, provider.Requests);
            Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, "runtime.pid")));
            Assert.IsFalse(
                Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
                    .Any(static path =>
                        Path.GetFileName(path).Contains("pipe", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task StartAsync_WorkspaceLockIgnoresDataDirectoryButAllowsDifferentWorkspaces()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var provider = new RecordingProvider();
            var firstWorkspace = Path.Combine(root, "workspace-a");
            var secondWorkspace = Path.Combine(root, "workspace-b");
            await using var first = await LocalRuntime.StartAsync(
                CreateOptions(firstWorkspace, Path.Combine(root, "data-a"), provider),
                _testContext.CancellationToken);
            await using var second = await LocalRuntime.StartAsync(
                CreateOptions(secondWorkspace, Path.Combine(root, "data-b"), provider),
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<WorkspaceBusyException>(
                () => LocalRuntime.StartAsync(
                    CreateOptions(firstWorkspace, Path.Combine(root, "data-c"), provider),
                    _testContext.CancellationToken));

            Assert.Contains("active Runtime instance", exception.Message, StringComparison.Ordinal);
            Assert.IsTrue(exception.ToError().IsRetryable);
            Assert.AreEqual(RuntimeErrorCodes.WorkspaceBusy, exception.ToError().Code);
            Assert.AreNotEqual(first.WorkspaceRoot, second.WorkspaceRoot);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task StartAsync_MemoryServiceForDifferentWorkspace_IsRejectedBeforeDataCreation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var options = CreateOptions(
                workspace,
                dataDirectory,
                new RecordingProvider()) with
            {
                MemoryFiles = new MemoryFileService(
                    Path.Combine(root, "home"),
                    Path.Combine(root, "other-workspace"))
            };

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => LocalRuntime.StartAsync(options, _testContext.CancellationToken));

            Assert.Contains("Runtime workspace", exception.Message, StringComparison.Ordinal);
            Assert.IsFalse(Directory.Exists(dataDirectory));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task StartAsync_HostServerAndLocalRuntimeShareWorkspaceLock()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var hostData = Path.Combine(root, "host-data");
            var hostOptions = new RuntimeServerOptions(workspace)
            {
                DataDirectory = hostData,
                ConfigDirectory = Path.Combine(hostData, "config"),
                LogDirectory = Path.Combine(hostData, "logs"),
                InstanceId = Guid.NewGuid().ToString("N"),
                HandshakeSecret = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ProviderResolver = null
            };
            await using var server = await RuntimeServer.StartAsync(
                hostOptions,
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<WorkspaceBusyException>(
                () => LocalRuntime.StartAsync(
                    CreateOptions(
                        workspace,
                        Path.Combine(root, "local-data"),
                        new RecordingProvider()),
                    _testContext.CancellationToken));

            Assert.Contains("active Runtime instance", exception.Message, StringComparison.Ordinal);
            Assert.IsTrue(exception.ToError().IsRetryable);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task LocalAndHostRuntimes_UseIdenticalMemoryWithoutHostConfigDependency()
    {
        var root = CreateTemporaryDirectory();
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        try
        {
            var userHome = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "workspace");
            var memoryFiles = new MemoryFileService(userHome, workspace);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Shared global rule.\n",
                _testContext.CancellationToken);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n\n- Shared project rule.\n",
                _testContext.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(userHome, ".madorin", "config.json"),
                "{ this personal provider configuration is intentionally invalid",
                _testContext.CancellationToken);
            var selection = CreateSelection();

            var localProvider = new RecordingProvider();
            await using (var localRuntime = await LocalRuntime.StartAsync(
                CreateOptions(
                    workspace,
                    Path.Combine(root, "local-data"),
                    localProvider) with
                {
                    MemoryFiles = memoryFiles
                },
                _testContext.CancellationToken))
            {
                _ = await CollectAsync(
                    localRuntime.RunAsync(
                        new NewSessionRunRequest(
                            Guid.NewGuid().ToString("N"),
                            Guid.NewGuid().ToString("N"),
                            RuntimeMode.Expert,
                            selection,
                            [new TextContentBlock("local")]),
                        _testContext.CancellationToken),
                    _testContext.CancellationToken);
            }

            var localRequest = Assert.ContainsSingle(localProvider.Requests);
            var localInstructions = GetSystemInstructions(localRequest);

            var hostProvider = new RecordingProvider();
            var hostData = Path.Combine(root, "host-data");
            var instanceId = Guid.NewGuid().ToString("N");
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    DataDirectory = hostData,
                    ConfigDirectory = Path.Combine(hostData, "config"),
                    LogDirectory = Path.Combine(hostData, "logs"),
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.ai.runtime.memory.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = userHome,
                    ProviderResolver = _ => hostProvider
                },
                _testContext.CancellationToken);
            var serverTask = server.RunAsync(serverCancellation.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _testContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);
            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    RuntimeMode.Expert,
                    selection,
                    [new TextContentBlock("host")]),
                timeout.Token);

            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType == MessageTypes.RunCompleted)
                {
                    break;
                }
            }

            var hostRequest = Assert.ContainsSingle(hostProvider.Requests);
            var hostInstructions = GetSystemInstructions(hostRequest);
            Assert.AreEqual(localInstructions, hostInstructions);
            var globalIndex = localInstructions.IndexOf(
                "Shared global rule.",
                StringComparison.Ordinal);
            var projectIndex = localInstructions.IndexOf(
                "Shared project rule.",
                StringComparison.Ordinal);
            Assert.IsGreaterThan(-1, globalIndex);
            Assert.IsGreaterThan(-1, projectIndex);
            Assert.IsLessThan(projectIndex, globalIndex);

            serverCancellation.Cancel();
            await serverTask;
        }
        finally
        {
            serverCancellation.Cancel();
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunAsync_ControlPeerAndLocalConsentHandler_AreMutuallyExclusive()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(dataDirectory);
            var memoryFiles = new MemoryFileService(Path.Combine(root, "home"), workspace);
            var limits = new RuntimeLimits();
            await using var blobStore = new BlobStagingStore(
                dataDirectory,
                limits,
                maxGlobalBlobBytes: 16 * 1024 * 1024);
            var core = new RuntimeExecutionCore(
                dataDirectory,
                $"Data Source={Path.Combine(dataDirectory, "runtime.db")}",
                "runtime-1",
                workspace,
                new AgentContextComposer(memoryFiles),
                memoryFiles,
                limits,
                blobStore);
            var catalogStore = new Madorin.AI.Runtime.Services.Tools.ToolCatalogStore(
                new Madorin.AI.Runtime.Tools.Builtin.BuiltinToolRegistry(),
                new JsonSchemaToolValidator());
            var events = core.RunAsync(
                "run-1",
                "session-1",
                new NewSessionRunRequest(
                    "request-1",
                    "turn-1",
                    RuntimeMode.Expert,
                    CreateSelection(),
                    [new TextContentBlock("test")]),
                new RecordingProvider(),
                catalogStore,
                catalogStore.CaptureSnapshot(),
                controlPeer: new NoopDuplexRpcPeer(),
                localToolConsentHandler: new DenyingConsentHandler(),
                ct: _testContext.CancellationToken);
            await using var enumerator = events.GetAsyncEnumerator(_testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await enumerator.MoveNextAsync());

            Assert.Contains("both a control peer", exception.Message, StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, "runtime.db")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static LocalRuntimeOptions CreateOptions(
        string workspace,
        string dataDirectory,
        IRuntimeProviderAdapter provider) =>
        new(
            workspace,
            dataDirectory,
            Path.Combine(dataDirectory, "logs"),
            Guid.NewGuid().ToString("N"),
            _ => provider);

    private static NextTurnSelection CreateSelection() =>
        new(
            1,
            RuntimeMode.Expert,
            new DefaultSelection("fake", "fake-model"),
            new ExpertModeOptions(
                new AgentRef(
                    "default",
                    "1.0",
                    "Preserve this system prompt exactly.",
                    "fake",
                    "fake-model")));

    private static string GetSystemInstructions(RuntimeProviderRequest request) => string.Join(
        '\n',
        request.Messages
            .Where(static message => message.Role == RuntimeProviderRoles.System)
            .SelectMany(static message => message.Content)
            .OfType<TextContentBlock>()
            .Select(static block => block.Text));

    private static async Task<List<RuntimeEventEnvelope>> CollectAsync(
        IAsyncEnumerable<RuntimeEventEnvelope> events,
        CancellationToken ct)
    {
        var result = new List<RuntimeEventEnvelope>();
        await foreach (var envelope in events.WithCancellation(ct))
        {
            result.Add(envelope);
        }

        return result;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-local-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class DenyingConsentHandler : IToolConsentHandler
    {
        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Denied));
        }

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ApprovalResponse(
                context.Request.CorrelationId,
                context.Request.ApprovalRequestId,
                ToolAuthorizationDecision.Denied));
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FailingProvider(RuntimeError error) : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new InvocationFailedProviderEvent(request.InvocationId, error);
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoopDuplexRpcPeer : IDuplexRpcPeer
    {
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<ReadOnlyMemory<byte>> SendAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        public void SetRequestHandler(
            Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
        }

        public ValueTask<JsonRpcResponse> SendRequestAsync(
            string method,
            JsonElement? parameters = null,
            CancellationToken ct = default) =>
            ValueTask.FromException<JsonRpcResponse>(new NotSupportedException());

        public ValueTask<JsonRpcResponse> SendRequestAsync(
            string method,
            JsonElement? parameters,
            TimeSpan timeout,
            CancellationToken ct = default) =>
            ValueTask.FromException<JsonRpcResponse>(new NotSupportedException());
    }

    private sealed class RecordingProvider : IRuntimeProviderAdapter
    {
        private int _invocationCount;

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var invocation = Interlocked.Increment(ref _invocationCount);
            yield return new TextDeltaProviderEvent(request.InvocationId, $"reply-{invocation}");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
