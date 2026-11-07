using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Cli.Tools;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Prompt;
using Madorin.AI.Runtime.Services.Tools;
using StandaloneSelection = Madorin.AI.Runtime.Cli.Config.ResolvedStandaloneSelection;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ServiceCommands
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static Command CreateServe(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var command = new Command("serve", "Start the persistent Runtime server.")
            .AddOptions(
                CommandOptions.Create<string?>(
                    "--config-dir",
                    "Override the configuration directory; defaults to <data-dir>/config."),
                CommandOptions.Create<string?>(
                    "--log-dir",
                    "Override the log directory; defaults to <data-dir>/logs."),
                CommandOptions.Create<string?>(
                    "--instance",
                    "Set the Runtime instance identifier; generated when omitted."),
                CommandOptions.Create<string?>(
                    "--pipe-prefix",
                    "Override the IPC pipe prefix; defaults to madorin.ai.runtime."),
                CommandOptions.Create<int?>(
                    "--max-runs",
                    "Set the maximum concurrent Run count; defaults to 4."),
                CommandOptions.Create<int?>(
                    "--http-port",
                    "Enable HTTP REST API on the specified port."));
        command.SetAction(async parseResult =>
        {
            var workspace = parseResult.GetValue(workspaceOption);
            if (string.IsNullOrWhiteSpace(workspace))
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "serve",
                    "--workspace is required.");
                return ExitCodes.InvalidArguments;
            }

            var workspaceRoot = Path.GetFullPath(workspace);
            var dataDirectory = parseResult.GetValue(dataDirectoryOption)
                ?? Path.Combine(workspaceRoot, ".madorin");
            if (!TryResolveRuntimeLogLevel(
                    parseResult.GetValue<string?>("--log-level"),
                    out var minimumLogLevel))
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "serve",
                    "--log-level must be Debug, Information, Warning, or Error.");
                return ExitCodes.InvalidArguments;
            }

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                CancelNoThrow(shutdown);
            };
            Console.CancelKeyPress += cancelHandler;
            using var terminateRegistration = OperatingSystem.IsWindows()
                ? null
                : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    CancelNoThrow(shutdown);
                });
            using var parentExitRegistration = ParentProcessExitRegistration.Track(shutdown);

            string handshakeSecret;
            try
            {
                handshakeSecret = await ReadHandshakeSecretAsync(input, shutdown.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                WriteError(parseResult, jsonOption, output, "serve", ex.Message);
                Console.CancelKeyPress -= cancelHandler;
                return ExitCodes.InvalidArguments;
            }

            var options = new RuntimeServerOptions(workspaceRoot)
            {
                DataDirectory = Path.GetFullPath(dataDirectory),
                ConfigDirectory = Path.GetFullPath(
                    parseResult.GetValue<string?>("--config-dir") ?? Path.Combine(dataDirectory, "config")),
                LogDirectory = Path.GetFullPath(
                    parseResult.GetValue<string?>("--log-dir") ?? Path.Combine(dataDirectory, "logs")),
                InstanceId = parseResult.GetValue<string?>("--instance") ?? string.Empty,
                PipePrefix = parseResult.GetValue<string?>("--pipe-prefix") ?? "madorin.ai.runtime",
                MaxConcurrentRuns = parseResult.GetValue<int?>("--max-runs") ?? 4,
                MinimumLogLevel = minimumLogLevel,
                HandshakeSecret = handshakeSecret,
                HttpPort = parseResult.GetValue<int?>("--http-port"),
                ProviderResolver = null
            };

            try
            {
                await using var server = await RuntimeServer.StartAsync(options, shutdown.Token);
                await server.RunAsync(shutdown.Token);
                WriteSuccess(parseResult, jsonOption, output, "serve");
                return ExitCodes.Success;
            }
            catch (InvalidOperationException ex)
            {
                WriteError(parseResult, jsonOption, output, "serve", ex.Message);
                return ExitCodes.WorkspaceError;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        });
        return command;
    }

    private static async Task<string> ReadHandshakeSecretAsync(
        TextReader input,
        CancellationToken cancellationToken)
    {
        var secret = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "A non-empty handshake secret is required on the controlled stdin handle.");
        }

        return secret.Trim();
    }

    public static Command CreateRun(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        ICliTerminal terminal,
        TextWriter output,
        TextWriter error,
        string? configDirectory = null,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory = null,
        string? memoryUserHome = null,
        IReplInterruptSource? replInterruptSource = null,
        bool showWelcome = false)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var providerOption = CommandOptions.Create<string?>("--provider", "Select a Provider.");
        var modelOption = CommandOptions.Create<string?>("--model", "Select a model.");
        var agentOption = CommandOptions.Create<string?>("--agent", "Select an Agent.");
        var modeOption = CommandOptions.Create<string?>(
            "--mode",
            "Select expert or standalone mode; defaults to expert.");
        var sessionOption = CommandOptions.Create<string?>("--session", "Continue an existing Session.");
        var inputOption = CommandOptions.Create<string?>("--input", "Run once with the supplied input.");
        var inputFileOption = CommandOptions.Create<string?>("--input-file", "Read input from a file.");
        var outputFileOption = CommandOptions.Create<string?>("--output-file", "Write output to a file.");
        var outputFormatOption = CommandOptions.Create<string?>(
            "--output-format",
            "Select text, json, or jsonl output; defaults to text (or json with --json).");
        var noStreamOption = CommandOptions.Create<bool>(
            "--no-stream",
            "Wait for the complete result; output streams by default.");
        var permissionModeOption = CommandOptions.Create<string?>(
            "--permission-mode",
            "Set permission mode: ask-all (default), auto-tools, or auto-all.");
        var instructionsModeOption = CommandOptions.Create<string?>(
            "--instructions-mode",
            "Set workspace instructions mode: off (default) or auto.");

        // Expert RequestControl options (passed via ExpertModeOptions.RequestControl)
        var temperatureOption = CommandOptions.Create<float?>("--temperature", "Sampling temperature (0.0-2.0).");
        var topPOption = CommandOptions.Create<float?>("--top-p", "Nucleus sampling (0.0-1.0).");
        var topKOption = CommandOptions.Create<int?>("--top-k", "Top-K sampling.");
        var maxTokensOption = CommandOptions.Create<int?>("--max-tokens", "Maximum tokens to generate.");
        var frequencyPenaltyOption = CommandOptions.Create<float?>("--frequency-penalty", "Frequency penalty (-2.0 to 2.0).");
        var presencePenaltyOption = CommandOptions.Create<float?>("--presence-penalty", "Presence penalty (-2.0 to 2.0).");
        var seedOption = CommandOptions.Create<int?>("--seed", "Random seed for deterministic generation.");
        var stopOption = CommandOptions.Create<string[]?>("--stop", "Stop sequences (repeatable).");
        var responseFormatOption = CommandOptions.Create<string?>("--response-format", "text | json (for structured output).");

        var command = new Command("run", "Run one task or enter interactive mode.")
            .AddOptions(
                providerOption,
                modelOption,
                agentOption,
                modeOption,
                sessionOption,
                inputOption,
                inputFileOption,
                outputFileOption,
                outputFormatOption,
                noStreamOption,
                CommandOptions.Create<int?>("--timeout", "Set the timeout in seconds."),
                CommandOptions.Create<int?>(
                    "--lock-wait",
                    "Seconds to wait when another Runtime holds the workspace lock."),
                CommandOptions.Create<string?>(
                    "--allow-tools",
                    "Comma-separated tool ids to allow without an interactive prompt."),
                permissionModeOption,
                instructionsModeOption,
                temperatureOption,
                topPOption,
                topKOption,
                maxTokensOption,
                frequencyPenaltyOption,
                presencePenaltyOption,
                seedOption,
                stopOption,
                responseFormatOption);

        command.SetAction(async parseResult =>
        {
            var directInput = parseResult.GetValue(inputOption);
            var inputFile = parseResult.GetValue(inputFileOption);
            var outputFile = parseResult.GetValue(outputFileOption);
            var outputFormatValue = parseResult.GetValue(outputFormatOption);
            var machineOutputRedirected = outputFile is not null;

            if (!TryResolveRunOutputFormat(
                    outputFormatValue,
                    CliOutput.IsJson(parseResult, jsonOption),
                    out var outputFormat,
                    out var outputFormatError))
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "InvalidOutputFormat",
                    outputFormatError);
                return ExitCodes.InvalidArguments;
            }

            if (!TryResolveRuntimeLogLevel(
                    parseResult.GetValue<string?>("--log-level"),
                    out var minimumLogLevel))
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "InvalidLogLevel",
                    "--log-level must be Debug, Information, Warning, or Error.");
                return ExitCodes.InvalidArguments;
            }

            if (directInput is not null && inputFile is not null)
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "ConflictingInputOptions",
                    "--input and --input-file cannot be used together.");
                return ExitCodes.InvalidArguments;
            }

            string? suppliedInput = directInput;
            if (inputFile is not null)
            {
                try
                {
                    suppliedInput = await ReadRunInputFileAsync(inputFile).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRunInputException(ex))
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        "InputFileError",
                        ex.Message);
                    return ExitCodes.InvalidArguments;
                }
            }

            if (suppliedInput is null && outputFile is not null)
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "OutputFileRequiresInput",
                    "--output-file requires --input or --input-file.");
                return ExitCodes.InvalidArguments;
            }

            if (suppliedInput is null
                && (outputFormat is not RunOutputFormat.Text || parseResult.GetValue(noStreamOption)))
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected: false,
                    "InteractiveOutputOptionsUnsupported",
                    "JSON, JSONL, and --no-stream require --input or --input-file.");
                return ExitCodes.InvalidArguments;
            }

            var allowToolsValue = parseResult.GetValue<string?>("--allow-tools");
            if (!StandaloneToolAllowList.TryParse(
                    allowToolsValue,
                    out var listedTools,
                    out var allowToolsError)
                && allowToolsValue is not null)
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "InvalidAllowTools",
                    allowToolsError ?? "--allow-tools is invalid.");
                return ExitCodes.InvalidArguments;
            }

            if (!StandaloneToolConsentPolicy.TryParsePermissionMode(
                    parseResult.GetValue(permissionModeOption),
                    out var permissionMode))
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "InvalidPermissionMode",
                    "--permission-mode must be ask-all, auto-tools, or auto-all.");
                return ExitCodes.InvalidArguments;
            }

            if (!TryParseWorkspaceInstructionsMode(
                    parseResult.GetValue(instructionsModeOption),
                    out var workspaceInstructionsEnabled))
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "InvalidInstructionsMode",
                    "--instructions-mode must be off or auto.");
                return ExitCodes.InvalidArguments;
            }

            if (listedTools is not null
                && permissionMode is not StandaloneToolPermissionMode.AskAll)
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "ConflictingPermissionOptions",
                    "--allow-tools cannot be combined with --permission-mode auto-tools or auto-all.");
                return ExitCodes.InvalidArguments;
            }

            var interactiveConsentEnabled =
                StandaloneToolConsentPolicy.IsInteractiveConsentEnabled(
                    terminal,
                    outputFormat is RunOutputFormat.Text,
                    outputFile is not null);
            if (permissionMode is StandaloneToolPermissionMode.AutoTools
                && !interactiveConsentEnabled)
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "InvalidPermissionMode",
                    "--permission-mode auto-tools requires an interactive text session without --output-file.");
                return ExitCodes.InvalidArguments;
            }

            AtomicOutputFile? atomicOutputFile = null;
            try
            {
                if (outputFile is not null)
                {
                    atomicOutputFile = AtomicOutputFile.Create(outputFile);
                }
            }
            catch (Exception ex) when (IsRunOutputException(ex))
            {
                RunOutputWriter.WriteCommandFailure(
                    output,
                    error,
                    outputFormat,
                    machineOutputRedirected,
                    "OutputFileError",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }

            await using var outputDestination = atomicOutputFile;
            var requestedWorkspace = parseResult.GetValue(workspaceOption);
            var requestedSessionId = parseResult.GetValue(sessionOption);
            var requestedProvider = parseResult.GetValue(providerOption);
            var requestedModel = parseResult.GetValue(modelOption);
            var requestedAgent = parseResult.GetValue(agentOption);
            var showWorkspaceWelcome = showWelcome;
            var announceWorkspaceChange = false;
            var workspaceInstructionsState = new WorkspaceInstructionsState(
                workspaceInstructionsEnabled);
            using var interactiveConsent = listedTools is null
                && (permissionMode is StandaloneToolPermissionMode.AutoAll
                    || interactiveConsentEnabled)
                ? new StandaloneToolConsentHandler(
                    terminal,
                    new StandaloneToolGrantFactory(),
                    permissionMode)
                : null;
            while (true)
            {
                StandaloneRuntimeContext context;
                try
                {
                    context = await StandaloneRuntimeContext.CreatePreparedAsync(
                        requestedWorkspace,
                        parseResult.GetValue(dataDirectoryOption),
                        configDirectory);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        "InvalidRuntimeContext",
                        ex.Message);
                    return ExitCodes.InvalidArguments;
                }
                catch (StandaloneWorkspaceDataMigrationException ex)
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        "WorkspaceDataUnavailable",
                        ex.Message);
                    return ExitCodes.WorkspaceError;
                }

                var loader = new StandaloneConfigLoader(context.ConfigDirectory);
                var resolver = new StandaloneConfigurationResolver(loader);
                StandaloneConfigurationSnapshot configuration;
                try
                {
                    try
                    {
                        configuration = await resolver.LoadAsync(context.WorkspaceRoot)
                            .ConfigureAwait(false);
                    }
                    catch (StandaloneConfigurationException ex)
                        when (ex.Code == StandaloneConfigurationResolver.ConfigurationNotFoundCode
                            && outputFormat is RunOutputFormat.Text)
                    {
                        _ = await ConfigWizard.RunInitAsync(
                                context.ConfigDirectory,
                                terminal)
                            .ConfigureAwait(false);
                        configuration = await resolver.LoadAsync(context.WorkspaceRoot)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (IsConfigurationException(ex))
                {
                    var code = ex is StandaloneConfigurationException configurationException
                        ? configurationException.Code
                        : StandaloneConfigurationResolver.ConfigurationInvalidCode;
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        code,
                        ex.Message);
                    return ExitCodes.InvalidArguments;
                }

                var config = configuration.GlobalConfig;
                var agents = configuration.Agents;
                StandaloneSelection selection;
                try
                {
                    selection = StandaloneConfigurationResolver.ResolveSelection(
                        configuration,
                        requestedProvider,
                        requestedModel,
                        requestedAgent);
                }
                catch (StandaloneConfigurationException ex)
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        ex.Code,
                        ex.Message);
                    return ExitCodes.InvalidArguments;
                }

                if (!TryResolveMode(parseResult.GetValue(modeOption), out var mode))
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        "InvalidMode",
                        "Mode must be expert or standalone.");
                    return ExitCodes.InvalidArguments;
                }

                var timeout = parseResult.GetValue<int?>("--timeout");
                using var cts = timeout is > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(timeout.Value))
                    : new CancellationTokenSource();
                var lockWait = parseResult.GetValue<int?>("--lock-wait");
                if (lockWait is < 0)
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        "InvalidLockWait",
                        "--lock-wait must be zero or a positive number of seconds.");
                    return ExitCodes.InvalidArguments;
                }

                // Collect Expert RequestControl from CLI (only meaningful for Expert mode)
                ExpertRequestControl? requestControl = null;
                var cliTemperature = parseResult.GetValue(temperatureOption);
                var cliTopP = parseResult.GetValue(topPOption);
                var cliTopK = parseResult.GetValue(topKOption);
                var cliMaxTokens = parseResult.GetValue(maxTokensOption);
                var cliFrequency = parseResult.GetValue(frequencyPenaltyOption);
                var cliPresence = parseResult.GetValue(presencePenaltyOption);
                var cliSeed = parseResult.GetValue(seedOption);
                var cliStop = parseResult.GetValue(stopOption);
                var cliResponseFormat = parseResult.GetValue(responseFormatOption);
                var cliNoStream = parseResult.GetValue(noStreamOption);

                if (cliTemperature.HasValue || cliTopP.HasValue || cliTopK.HasValue || cliMaxTokens.HasValue ||
                    cliFrequency.HasValue || cliPresence.HasValue || cliSeed.HasValue ||
                    (cliStop is { Length: > 0 }) || !string.IsNullOrWhiteSpace(cliResponseFormat) || cliNoStream)
                {
                    requestControl = new ExpertRequestControl
                    {
                        Temperature = cliTemperature,
                        TopP = cliTopP,
                        TopK = cliTopK,
                        MaxTokens = cliMaxTokens,
                        FrequencyPenalty = cliFrequency,
                        PresencePenalty = cliPresence,
                        Seed = cliSeed,
                        StopSequences = cliStop is { Length: > 0 } ? cliStop : null,
                        ResponseFormat = string.IsNullOrWhiteSpace(cliResponseFormat) ? null : cliResponseFormat.Trim(),
                        Stream = cliNoStream ? false : null
                    };
                }

                LocalRuntime runtime;
                try
                {
                    var workerSupervisor = new ProviderWorkerSupervisor();
                    var providerResolver = providerResolverFactory is null
                        ? StandaloneProviderFactory.Create(config, workerSupervisor)
                        : providerResolverFactory(config);
                    var resolvedMemoryUserHome = memoryUserHome
                        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    ArgumentException.ThrowIfNullOrWhiteSpace(resolvedMemoryUserHome);
                    var memoryFiles = new MemoryFileService(
                        Path.GetFullPath(resolvedMemoryUserHome),
                        context.WorkspaceRoot);
                    IToolConsentHandler? consentHandler = listedTools is null
                        ? interactiveConsent
                        : listedTools;
                    runtime = await LocalRuntime.StartAsync(
                            new LocalRuntimeOptions(
                                context.WorkspaceRoot,
                                context.DataDirectory,
                                context.LogDirectory,
                                context.RuntimeInstanceId,
                                providerResolver)
                            {
                                MemoryFiles = memoryFiles,
                                ToolConsentHandler = consentHandler,
                                WorkspaceInstructionsState = workspaceInstructionsState,
                                WorkerSupervisor = workerSupervisor,
                                MinimumLogLevel = minimumLogLevel,
                                WorkspaceLockTimeout = lockWait is > 0
                                    ? TimeSpan.FromSeconds(lockWait.Value)
                                    : TimeSpan.Zero
                            },
                            cts.Token)
                        .ConfigureAwait(false);

                    await using (runtime.ConfigureAwait(false))
                    {
                        var sessionId = requestedSessionId;
                        if (suppliedInput is not null)
                        {
                            var events = sessionId is null
                                ? runtime.RunAsync(
                                    BuildNewRunRequest(
                                        suppliedInput,
                                        mode,
                                        selection,
                                        config,
                                        agents,
                                        requestControl: requestControl),
                                    cts.Token)
                                : runtime.RunAsync(
                                    BuildExistingRunRequest(
                                        sessionId,
                                        suppliedInput,
                                        mode,
                                        selection,
                                        config,
                                        agents,
                                        requestControl: requestControl),
                                    cts.Token);
                            var runOutput = outputDestination?.Writer ?? output;
                            var runOutputWriter = new RunOutputWriter(
                                runOutput,
                                error,
                                outputFormat,
                                parseResult.GetValue(noStreamOption),
                                machineOutputRedirected,
                                message => RedactSecrets(message, config));
                            var result = await ExecuteOneRunAsync(
                                    events,
                                    runOutputWriter,
                                    cts.Token)
                                .ConfigureAwait(false);
                            if (result.ExitCode == ExitCodes.Success && outputDestination is not null)
                            {
                                try
                                {
                                    await outputDestination.CommitAsync(cts.Token).ConfigureAwait(false);
                                }
                                catch (Exception ex) when (IsRunOutputException(ex))
                                {
                                    RunOutputWriter.WriteCommandFailure(
                                        output,
                                        error,
                                        outputFormat,
                                        machineOutputRedirected,
                                        "OutputFileCommitError",
                                        ex.Message);
                                    return ExitCodes.WorkspaceError;
                                }
                            }

                            return result.ExitCode;
                        }

                        if (announceWorkspaceChange)
                        {
                            terminal.Write("工作区：", CliTextStyle.Muted);
                            terminal.WriteLine(context.WorkspaceRoot, CliTextStyle.Selected);
                        }

                        var replResult = await RunReplAsync(
                                terminal,
                                output,
                                error,
                                runtime,
                                interactiveConsent,
                                workspaceInstructionsState,
                                memoryFiles,
                                sessionId,
                                mode,
                                selection,
                                agents,
                                config,
                                replInterruptSource ?? ConsoleReplInterruptSource.Instance,
                                showWorkspaceWelcome,
                                cts.Token)
                            .ConfigureAwait(false);
                        if (replResult.WorkspaceRoot is null)
                        {
                            return replResult.ExitCode;
                        }

                        requestedWorkspace = replResult.WorkspaceRoot;
                        requestedSessionId = null;
                        requestedProvider = replResult.Selection.Provider.Name;
                        requestedModel = replResult.Selection.Model;
                        requestedAgent = replResult.Selection.Agent.Id;
                        showWorkspaceWelcome = false;
                        announceWorkspaceChange = true;
                    }
                }
                catch (WorkspaceBusyException ex)
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        ex.ToError().Code,
                        RedactSecrets(ex.Message, config),
                        isRetryable: true);
                    return ExitCodes.WorkspaceError;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    RunOutputWriter.WriteCommandFailure(
                        output,
                        error,
                        outputFormat,
                        machineOutputRedirected,
                        "RuntimeError",
                        RedactSecrets(ex.Message, config));
                    return ExitCodes.WorkspaceError;
                }
            }
        });

        return command;
    }

    private static async Task<ReplLoopResult> RunReplAsync(
        ICliTerminal terminal,
        TextWriter output,
        TextWriter error,
        LocalRuntime runtime,
        StandaloneToolConsentHandler? interactiveConsent,
        WorkspaceInstructionsState workspaceInstructionsState,
        MemoryFileService memoryFiles,
        string? sessionId,
        RuntimeMode mode,
        StandaloneSelection selection,
        IReadOnlyList<AgentConfig> agents,
        StandaloneConfig config,
        IReplInterruptSource interruptSource,
        bool showWelcome,
        CancellationToken ct)
    {
        ReplLoopResult Complete(int exitCode) => new(exitCode, null, selection);

        var selectionVersion = 1;
        var helpLanguage = ReplHelpLanguage.Chinese;
        if (sessionId is not null)
        {
            var snapshot = await runtime.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                await error.WriteLineAsync($"Session '{sessionId}' was not found.")
                    .ConfigureAwait(false);
                return Complete(ExitCodes.InvalidArguments);
            }

            if (snapshot.Mode != mode)
            {
                await error.WriteLineAsync(
                        $"Session '{sessionId}' uses {snapshot.Mode} mode; the current REPL uses {mode} mode.")
                    .ConfigureAwait(false);
                return Complete(ExitCodes.InvalidArguments);
            }

            selectionVersion = snapshot.SelectionVersion ?? selectionVersion;
        }

        if (showWelcome)
        {
            ReplWelcomeWriter.Write(
                terminal,
                new ReplWelcomeSnapshot(
                    RuntimeVersionInfoProvider.Create().Version,
                    mode,
                    selection.Agent.Id,
                    selection.Agent.Name,
                    selection.Provider.Name,
                    selection.Model,
                    runtime.WorkspaceRoot));
        }

        using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interruptSync = new object();
        var interruptState = ReplInterruptState.Idle;
        CancellationTokenSource? activeRunCts = null;
        string? activeMeetingSessionId = sessionId;
        using var interruptSubscription = interruptSource.Subscribe(() =>
        {
            CancellationTokenSource? cancellationTarget = null;
            var shouldExit = false;
            lock (interruptSync)
            {
                switch (interruptState)
                {
                    case ReplInterruptState.Idle:
                        interruptState = ReplInterruptState.Exiting;
                        shouldExit = true;
                        break;
                    case ReplInterruptState.Running:
                        interruptState = ReplInterruptState.Cancelling;
                        cancellationTarget = activeRunCts;
                        break;
                    case ReplInterruptState.Pausing:
                        interruptState = ReplInterruptState.Cancelling;
                        cancellationTarget = activeRunCts;
                        break;
                    case ReplInterruptState.Cancelling:
                    case ReplInterruptState.Exiting:
                        interruptState = ReplInterruptState.Exiting;
                        shouldExit = true;
                        break;
                }
            }

            TryCancel(cancellationTarget);
            if (shouldExit)
            {
                TryCancel(exitCts);
            }
        });

        try
        {
            while (true)
            {
                exitCts.Token.ThrowIfCancellationRequested();
                lock (interruptSync)
                {
                    if (interruptState == ReplInterruptState.Exiting)
                    {
                        return Complete(ExitCodes.UserInterrupted);
                    }

                    interruptState = ReplInterruptState.Idle;
                }

                terminal.Write("madorin", CliTextStyle.Prompt);
                terminal.Write(" [", CliTextStyle.Muted);
                terminal.Write($"{selection.Provider.Name}/{selection.Model}", CliTextStyle.Selected);
                terminal.WriteLine("]", CliTextStyle.Muted);

                var userInput = await terminal.ReadBlockAsync(exitCts.Token).ConfigureAwait(false);
                if (userInput is null)
                {
                    return Complete(ExitCodes.Success);
                }

                if (string.IsNullOrWhiteSpace(userInput))
                {
                    continue;
                }

                var isSingleLine = !userInput.Contains('\n');
                var commandResult = isSingleLine
                    ? await TryHandleCoreReplCommandAsync(
                        userInput,
                        terminal,
                        output,
                        error,
                        runtime,
                        interactiveConsent,
                        workspaceInstructionsState,
                        sessionId,
                        mode,
                        selection,
                        selectionVersion,
                        agents,
                        config,
                        helpLanguage,
                        exitCts.Token)
                        .ConfigureAwait(false)
                    : null;
                if (commandResult is not null)
                {
                    helpLanguage = commandResult.HelpLanguage;
                    sessionId = commandResult.SessionId;
                    selection = commandResult.Selection;
                    selectionVersion = commandResult.SelectionVersion;
                    if (commandResult.WorkspaceRoot is { } workspaceRoot)
                    {
                        return new ReplLoopResult(
                            ExitCodes.Success,
                            workspaceRoot,
                            selection);
                    }

                    if (commandResult.ExitCode is { } commandExitCode)
                    {
                        return Complete(commandExitCode);
                    }

                    continue;
                }

                if (isSingleLine
                    && await TryHandleMemoryReplCommandAsync(
                        userInput,
                        terminal,
                        output,
                        memoryFiles,
                        exitCts.Token)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(
                    exitCts.Token);
                lock (interruptSync)
                {
                    if (interruptState == ReplInterruptState.Exiting)
                    {
                        return Complete(ExitCodes.UserInterrupted);
                    }

                    activeRunCts = runCts;
                    interruptState = ReplInterruptState.Running;
                    activeMeetingSessionId = sessionId;
                }

                var events = sessionId is null
                    ? runtime.RunAsync(
                        BuildNewRunRequest(
                            userInput,
                            mode,
                            selection,
                            config,
                            agents,
                            selectionVersion),
                        runCts.Token)
                    : runtime.RunAsync(
                        BuildExistingRunRequest(
                            sessionId,
                            userInput,
                            mode,
                            selection,
                            config,
                            agents,
                            selectionVersion),
                        runCts.Token);
                var replyOutput = terminal.IsInteractive
                    ? new TerminalStyleTextWriter(
                        terminal,
                        CliTextStyle.Assistant,
                        output,
                        () => terminal.WriteLine(
                            DateTime.Now.ToString("HH:mm:ss"),
                            CliTextStyle.Muted))
                    : output;
                using var activity = terminal.IsInteractive
                    ? new ReplThinkingStatus(terminal)
                    : null;
                if (interactiveConsent is not null)
                {
                    interactiveConsent.Activity = activity;
                }

                activity?.Start();
                var runOutputWriter = new RunOutputWriter(
                    replyOutput,
                    error,
                    RunOutputFormat.Text,
                    noStream: false,
                    machineOutputRedirected: false,
                    message => RedactSecrets(message, config),
                    activity);
                RunOutputResult result;
                try
                {
                    result = await ExecuteOneRunAsync(
                            events,
                            runOutputWriter,
                            exitCts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (interactiveConsent is not null)
                    {
                        interactiveConsent.Activity = null;
                    }
                }

                bool continueAfterCancellation;
                var wasPausing = false;
                lock (interruptSync)
                {
                    activeRunCts = null;
                    wasPausing = interruptState == ReplInterruptState.Pausing;
                    continueAfterCancellation =
                        interruptState is (ReplInterruptState.Cancelling or ReplInterruptState.Pausing)
                        && !exitCts.IsCancellationRequested;
                    interruptState = exitCts.IsCancellationRequested
                        ? ReplInterruptState.Exiting
                        : ReplInterruptState.Idle;
                }

                sessionId = result.SessionId ?? sessionId;
                activeMeetingSessionId = sessionId;
                if (exitCts.IsCancellationRequested)
                {
                    return Complete(ExitCodes.UserInterrupted);
                }

                if (result.ExitCode == ExitCodes.UserInterrupted
                    && (continueAfterCancellation || wasPausing))
                {
                    continue;
                }

                if (result.ExitCode == ExitCodes.GeneralError)
                {
                    continue;
                }

                if (result.ExitCode != ExitCodes.Success)
                {
                    return Complete(result.ExitCode);
                }
            }
        }
        catch (OperationCanceledException) when (exitCts.IsCancellationRequested)
        {
            return Complete(ExitCodes.UserInterrupted);
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is not null)
        {
            CancelNoThrow(cancellationTokenSource);
        }
    }

    private static async Task<ReplCommandResult?> TryHandleCoreReplCommandAsync(
        string command,
        ICliTerminal terminal,
        TextWriter output,
        TextWriter error,
        LocalRuntime runtime,
        StandaloneToolConsentHandler? interactiveConsent,
        WorkspaceInstructionsState workspaceInstructionsState,
        string? sessionId,
        RuntimeMode mode,
        StandaloneSelection selection,
        int selectionVersion,
        IReadOnlyList<AgentConfig> agents,
        StandaloneConfig config,
        ReplHelpLanguage helpLanguage,
        CancellationToken ct)
    {
        if (!command.StartsWith('/'))
        {
            return null;
        }

        var separator = command.IndexOf(' ');
        var commandName = separator < 0 ? command : command[..separator];
        var argument = separator < 0 ? null : command[(separator + 1)..].Trim();
        if (argument is { Length: 0 })
        {
            argument = null;
        }

        ReplCommandResult Current(int? exitCode = null) =>
            new(sessionId, selection, selectionVersion, exitCode, HelpLanguage: helpLanguage);

        try
        {
            switch (commandName.ToUpperInvariant())
            {
                case "/EXIT":
                case "/QUIT":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync($"Usage: {commandName}").ConfigureAwait(false);
                        return Current();
                    }

                    return Current(ExitCodes.Success);

                case "/MODEL":
                    if (argument is null)
                    {
                        if (selection.Provider.Models.Count == 0)
                        {
                            await error.WriteLineAsync(
                                    $"Provider '{selection.Provider.Name}' has no available models.")
                                .ConfigureAwait(false);
                            return Current();
                        }

                        argument = await ReplSelectionMenu.PromptAsync(
                                terminal,
                                $"Models for Provider '{selection.Provider.Name}':",
                                "model",
                                selection.Provider.Models
                                    .Select(static model => (model, (string?)null))
                                    .ToArray(),
                                selection.Model,
                                ct)
                            .ConfigureAwait(false);
                        if (argument is null)
                        {
                            return Current();
                        }
                    }

                    if (!StandaloneConfigurationResolver.TryResolveGlobalSelection(
                            config,
                            agents,
                            selection.Provider.Name,
                            argument,
                            selection.Agent.Id,
                            out var modelSelection))
                    {
                        await error.WriteLineAsync(
                                $"Model '{argument}' is not available from Provider '{selection.Provider.Name}'.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    return await UpdateReplSelectionAsync(
                            runtime,
                            terminal,
                            sessionId,
                            mode,
                            modelSelection,
                            selectionVersion,
                            agents,
                            config,
                            ct)
                        .ConfigureAwait(false);

                case "/PROVIDER":
                    if (argument is null)
                    {
                        if (config.Providers.Count == 0)
                        {
                            await error.WriteLineAsync("No Providers are configured.")
                                .ConfigureAwait(false);
                            return Current();
                        }

                        argument = await ReplSelectionMenu.PromptAsync(
                                terminal,
                                "Providers:",
                                "provider",
                                config.Providers
                                    .Select(static provider =>
                                        (
                                            provider.Name,
                                            (string?)($"{provider.Models.Count} " +
                                                (provider.Models.Count == 1 ? "model" : "models"))
                                        ))
                                    .ToArray(),
                                selection.Provider.Name,
                                ct)
                            .ConfigureAwait(false);
                        if (argument is null)
                        {
                            return Current();
                        }
                    }

                    var provider = config.Providers.FirstOrDefault(candidate =>
                        candidate.Name.Equals(argument, StringComparison.Ordinal));
                    if (provider is null)
                    {
                        await error.WriteLineAsync($"Provider '{argument}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var providerModel = provider.Models.Contains(
                        selection.Model,
                        StringComparer.Ordinal)
                        ? selection.Model
                        : provider.Models.Contains(config.DefaultModel, StringComparer.Ordinal)
                            ? config.DefaultModel
                            : provider.Models.FirstOrDefault();
                    if (providerModel is null
                        || !StandaloneConfigurationResolver.TryResolveGlobalSelection(
                            config,
                            agents,
                            provider.Name,
                            providerModel,
                            selection.Agent.Id,
                            out var providerSelection))
                    {
                        await error.WriteLineAsync(
                                $"Provider '{argument}' has no usable model for Agent '{selection.Agent.Id}'.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    return await UpdateReplSelectionAsync(
                            runtime,
                            terminal,
                            sessionId,
                            mode,
                            providerSelection,
                            selectionVersion,
                            agents,
                            config,
                            ct)
                        .ConfigureAwait(false);

                case "/AGENT":
                    if (argument is null)
                    {
                        if (agents.Count == 0)
                        {
                            await error.WriteLineAsync("No Agents are configured.")
                                .ConfigureAwait(false);
                            return Current();
                        }

                        argument = await ReplSelectionMenu.PromptAsync(
                                terminal,
                                "Agents:",
                                "agent",
                                agents
                                    .Select(static agent => (agent.Id, (string?)agent.Name))
                                    .ToArray(),
                                selection.Agent.Id,
                                ct)
                            .ConfigureAwait(false);
                        if (argument is null)
                        {
                            return Current();
                        }
                    }

                    if (!StandaloneConfigurationResolver.TryResolveGlobalSelection(
                            config,
                            agents,
                            providerOverride: null,
                            modelOverride: null,
                            argument,
                            out var agentSelection))
                    {
                        await error.WriteLineAsync(
                                $"Agent '{argument}' does not have a valid Provider and model.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    return await UpdateReplSelectionAsync(
                            runtime,
                            terminal,
                            sessionId,
                            mode,
                            agentSelection,
                            selectionVersion,
                            agents,
                            config,
                            ct)
                        .ConfigureAwait(false);

                case "/MODE":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync(
                                "The Session mode cannot be changed inside the REPL.")
                            .ConfigureAwait(false);
                    }

                    await output.WriteLineAsync($"Mode: {mode}").ConfigureAwait(false);
                    return Current();

                case "/INSTRUCTIONS":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /instructions").ConfigureAwait(false);
                        return Current();
                    }

                    var selectedWorkspaceInstructions = await PromptForWorkspaceInstructionsAsync(
                            terminal,
                            workspaceInstructionsState.Enabled,
                            ct)
                        .ConfigureAwait(false);
                    if (selectedWorkspaceInstructions is null)
                    {
                        return Current();
                    }

                    workspaceInstructionsState.SetEnabled(selectedWorkspaceInstructions.Value);
                    await WriteWorkspaceInstructionsStatusAsync(
                            output,
                            workspaceInstructionsState.Enabled,
                            runtime.WorkspaceRoot,
                            ct)
                        .ConfigureAwait(false);
                    return Current();

                case "/PERMISSION":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /permission").ConfigureAwait(false);
                        return Current();
                    }

                    if (interactiveConsent is null)
                    {
                        await error.WriteLineAsync(
                                "Permission mode cannot be changed because --allow-tools is active or interactive consent is unavailable.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var selectedPermissionMode = await PromptForPermissionModeAsync(
                            terminal,
                            interactiveConsent.PermissionMode,
                            ct)
                        .ConfigureAwait(false);
                    if (selectedPermissionMode is null)
                    {
                        return Current();
                    }

                    interactiveConsent.SetPermissionMode(selectedPermissionMode.Value);
                    if (selectedPermissionMode is StandaloneToolPermissionMode.AutoAll)
                    {
                        terminal.WriteLine(
                            "Warning: all tool and workflow requests will be approved automatically for this CLI session.",
                            CliTextStyle.Warning);
                    }

                    await output.WriteLineAsync(
                            $"Permission mode: {StandaloneToolConsentPolicy.GetPermissionModeName(selectedPermissionMode.Value)}")
                        .ConfigureAwait(false);
                    return Current();

                case "/SESSION":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /session").ConfigureAwait(false);
                        return Current();
                    }

                    if (sessionId is null)
                    {
                        await output.WriteLineAsync("Session: (new)").ConfigureAwait(false);
                        return Current();
                    }

                    var snapshot = await runtime.GetSessionSnapshotAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        await error.WriteLineAsync($"Session '{sessionId}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    await output.WriteLineAsync($"Session: {snapshot.SessionId}")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Mode: {snapshot.Mode}").ConfigureAwait(false);
                    await output.WriteLineAsync($"Status: {snapshot.Status}").ConfigureAwait(false);
                    await output.WriteLineAsync($"Provider: {selection.Provider.Name}")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Model: {selection.Model}").ConfigureAwait(false);
                    await output.WriteLineAsync($"Agent: {selection.Agent.Id}").ConfigureAwait(false);
                    return Current();

                case "/NEW":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /new").ConfigureAwait(false);
                        return Current();
                    }

                    await output.WriteLineAsync("A new Session will be created on the next turn.")
                        .ConfigureAwait(false);
                    return new ReplCommandResult(null, selection, 1, ExitCode: null);

                case "/PAUSE":
                {
                    await error.WriteLineAsync("/pause command has been removed (Meeting mode discontinued).")
                        .ConfigureAwait(false);
                    return Current();
                }

                case "/INTERRUPT":
                {
                    await error.WriteLineAsync("/interrupt command has been removed (Meeting mode discontinued).")
                        .ConfigureAwait(false);
                    return Current();
                }

                case "/RESUME":
                    if (argument is null)
                    {
                        var sessions = await runtime.ListSessionsAsync(
                                new SessionListParameters(Mode: mode, Limit: 10),
                                ct)
                            .ConfigureAwait(false);
                        if (sessions.Sessions.Length == 0)
                        {
                            await output.WriteLineAsync("No resumable Sessions were found.")
                                .ConfigureAwait(false);
                            return Current();
                        }

                        foreach (var item in sessions.Sessions)
                        {
                            await output.WriteLineAsync(
                                    $"{item.SessionId}  {item.Mode}  {item.Status}  {item.UpdatedAt:O}")
                                .ConfigureAwait(false);
                        }

                        return Current();
                    }

                    var resumeSnapshot = await runtime.GetSessionSnapshotAsync(argument, ct)
                        .ConfigureAwait(false);
                    if (resumeSnapshot is null)
                    {
                        await error.WriteLineAsync($"Session '{argument}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    if (resumeSnapshot.Mode != mode)
                    {
                        await error.WriteLineAsync(
                                $"Session '{argument}' uses {resumeSnapshot.Mode} mode; the current REPL uses {mode} mode.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var savedSelection = await runtime.GetSessionSelectionAsync(argument, ct)
                        .ConfigureAwait(false);
                    if (savedSelection is null
                        || !StandaloneConfigurationResolver.TryResolveGlobalSelection(
                            config,
                            agents,
                            savedSelection.DefaultSelection.ProviderId,
                            savedSelection.DefaultSelection.ModelId,
                            GetPrimaryAgentId(savedSelection.ModeOptions),
                            out var resumedSelection))
                    {
                        await error.WriteLineAsync(
                                $"Session '{argument}' references an Agent, Provider, or model that is not available locally.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    await output.WriteLineAsync($"Resumed Session: {argument}")
                        .ConfigureAwait(false);
                    return new ReplCommandResult(
                        argument,
                        resumedSelection,
                        resumeSnapshot.SelectionVersion ?? savedSelection.SelectionVersion,
                        ExitCode: null);

                case "/COMPACT":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /compact").ConfigureAwait(false);
                        return Current();
                    }

                    if (sessionId is null)
                    {
                        await error.WriteLineAsync("No active Session. Submit a turn before using /compact.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var compaction = await runtime.CompactSessionAsync(
                            sessionId,
                            new SessionCompactionOptions(),
                            ct)
                        .ConfigureAwait(false);
                    if (compaction is null)
                    {
                        await error.WriteLineAsync($"Session '{sessionId}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    await output.WriteLineAsync(
                            $"Compacted Session: {compaction.SessionId} ({compaction.Strategy})")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(
                            $"Messages: {compaction.SourceMessageCount} -> {compaction.ProjectedMessageCount}")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(
                            $"Estimated tokens: {compaction.BeforeEstimatedTokens} -> {compaction.AfterEstimatedTokens}")
                        .ConfigureAwait(false);
                    return Current();

                case "/CONTEXT":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /context").ConfigureAwait(false);
                        return Current();
                    }

                    if (sessionId is null)
                    {
                        await output.WriteLineAsync(
                                $"CanonicalHistory: 0 messages | about {TokenCountFormat.Format(0)} tokens")
                            .ConfigureAwait(false);
                        await output.WriteLineAsync(
                                $"ContextProjection: 0 messages | about {TokenCountFormat.Format(0)} tokens (full)")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var contextStatus = await runtime.GetSessionContextStatusAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    if (contextStatus is null)
                    {
                        await error.WriteLineAsync($"Session '{sessionId}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    await output.WriteLineAsync(
                            $"CanonicalHistory: {contextStatus.CanonicalMessageCount} messages | " +
                            $"about {TokenCountFormat.Format(contextStatus.CanonicalEstimatedTokens)} tokens")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(
                            $"ContextProjection: {contextStatus.ProjectionMessageCount} messages | " +
                            $"about {TokenCountFormat.Format(contextStatus.ProjectionEstimatedTokens)} tokens " +
                            $"({contextStatus.ProjectionStrategy})")
                        .ConfigureAwait(false);
                    return Current();

                case "/EXPORT":
                    if (sessionId is null)
                    {
                        await error.WriteLineAsync("No active Session. Submit a turn before using /export.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var exportSnapshot = await runtime.GetSessionSnapshotAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    if (exportSnapshot is null)
                    {
                        await error.WriteLineAsync($"Session '{sessionId}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var exportRecords = await runtime.ReadSessionHistoryAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    if (argument is null)
                    {
                        await SessionExportWriter.WriteAsync(
                                output,
                                exportSnapshot,
                                exportRecords,
                                SessionExportFormat.Markdown,
                                includeReasoning: false,
                                includeToolCalls: false,
                                ct)
                            .ConfigureAwait(false);
                        return Current();
                    }

                    await using (var exportFile = AtomicOutputFile.Create(argument))
                    {
                        await SessionExportWriter.WriteAsync(
                                exportFile.Writer,
                                exportSnapshot,
                                exportRecords,
                                SessionExportFormat.Markdown,
                                includeReasoning: false,
                                includeToolCalls: false,
                                ct)
                            .ConfigureAwait(false);
                        await exportFile.CommitAsync(ct).ConfigureAwait(false);
                    }

                    await output.WriteLineAsync($"Exported Session: {Path.GetFullPath(argument)}")
                        .ConfigureAwait(false);
                    return Current();

                case "/HISTORY":
                    if (sessionId is null)
                    {
                        await output.WriteLineAsync("No messages in the current Session.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var includeAllHistory = argument?.Equals("all", StringComparison.OrdinalIgnoreCase) == true;
                    var historyCount = 5;
                    if (argument is not null
                        && !includeAllHistory
                        && (!int.TryParse(argument, out historyCount) || historyCount <= 0))
                    {
                        await error.WriteLineAsync("Usage: /history [positive-count|all]")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var historySnapshot = await runtime.GetSessionSnapshotAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    if (historySnapshot is null)
                    {
                        await error.WriteLineAsync($"Session '{sessionId}' was not found.")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    var historyRecords = await runtime.ReadSessionHistoryAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    var visibleHistory = includeAllHistory
                        ? historyRecords
                        : historyRecords.TakeLast(historyCount).ToArray();
                    await SessionExportWriter.WriteAsync(
                            output,
                            historySnapshot,
                            visibleHistory,
                            SessionExportFormat.Text,
                            includeReasoning: false,
                            includeToolCalls: false,
                            ct)
                        .ConfigureAwait(false);
                    return Current();

                case "/CLEAR":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /clear").ConfigureAwait(false);
                        return Current();
                    }

                    terminal.Clear();
                    return Current();

                case "/CD":
                    if (argument is null)
                    {
                        await output.WriteLineAsync($"工作区：{runtime.WorkspaceRoot}")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    if (!TryResolveReplWorkspace(
                            runtime.WorkspaceRoot,
                            argument,
                            out var nextWorkspace,
                            out var workspaceError))
                    {
                        await error.WriteLineAsync(workspaceError).ConfigureAwait(false);
                        return Current();
                    }

                    if (ReplPathsEqual(runtime.WorkspaceRoot, nextWorkspace))
                    {
                        await output.WriteLineAsync($"工作区：{runtime.WorkspaceRoot}")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    await output.WriteLineAsync($"正在切换工作区：{nextWorkspace}")
                        .ConfigureAwait(false);
                    return new ReplCommandResult(
                        sessionId,
                        selection,
                        selectionVersion,
                        ExitCode: null,
                        WorkspaceRoot: nextWorkspace);

                case "/STATUS":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /status").ConfigureAwait(false);
                        return Current();
                    }

                    var runtimeStatus = runtime.GetStatus();
                    await output.WriteLineAsync($"Runtime: {runtimeStatus.RuntimeInstanceId}")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Workspace: {runtimeStatus.WorkspaceRoot}")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Active runs: {runtimeStatus.ActiveRunCount}")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Managed memory: {runtimeStatus.ManagedMemoryBytes} bytes")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Tools: {runtimeStatus.ToolCount}")
                        .ConfigureAwait(false);
                    return Current();

                case "/TOOLS":
                    if (argument is not null)
                    {
                        await error.WriteLineAsync("Usage: /tools").ConfigureAwait(false);
                        return Current();
                    }

                    foreach (var tool in runtime.GetToolCatalogSnapshot().Tools)
                    {
                        var access = tool.ToolId switch
                        {
                            "mcli_memory_read" => "read-only",
                            "mcli_memory_write"
                                or "mcli_memory_append"
                                or "mcli_memory_update"
                                or "mcli_memory_delete" => "approval required",
                            _ when tool.RequiresApproval => "approval required",
                            _ => tool.Risk.ToString()
                        };
                        await output.WriteLineAsync($"{tool.ToolId} [{access}]")
                            .ConfigureAwait(false);
                    }

                    return Current();

                case "/SKILL":
                    await HandleSkillReplCommandAsync(runtime, output, error, argument, ct)
                        .ConfigureAwait(false);
                    return Current();

                case "/HELP":
                    if (!ReplHelpWriter.Write(terminal, argument, helpLanguage, out var nextLanguage))
                    {
                        await error.WriteLineAsync(
                                nextLanguage == ReplHelpLanguage.English
                                    ? $"Unknown command '{argument}'. Use /help to list commands."
                                    : $"没有这个命令：{argument}。用 /help 查看命令。")
                            .ConfigureAwait(false);
                        return Current();
                    }

                    return Current() with { HelpLanguage = nextLanguage };

                case "/MEMORY":
                case "/REMEMBER":
                    return null;

                default:
                    await error.WriteLineAsync(
                            helpLanguage == ReplHelpLanguage.English
                                ? $"Unknown command '{commandName}'. Use /help to list commands."
                                : $"没有这个命令：{commandName}。用 /help 查看命令。")
                        .ConfigureAwait(false);
                    return Current();
            }
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or KeyNotFoundException
            or NotSupportedException
            or TimeoutException
            or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"REPL command failed: {RedactSecrets(ex.Message, config)}")
                .ConfigureAwait(false);
            return Current();
        }
    }

    internal static async Task<StandaloneToolPermissionMode?> PromptForPermissionModeAsync(
        ICliTerminal terminal,
        StandaloneToolPermissionMode current,
        CancellationToken ct)
    {
        const int maxInputAttempts = 3;
        terminal.WriteLine("Permission mode:", CliTextStyle.Title);
        WritePermissionModeOption(
            terminal,
            1,
            "Ask every time",
            current is StandaloneToolPermissionMode.AskAll);
        WritePermissionModeOption(
            terminal,
            2,
            "Auto-approve tools (workflow approvals still ask)",
            current is StandaloneToolPermissionMode.AutoTools);
        WritePermissionModeOption(
            terminal,
            3,
            "Auto-approve everything (no confirmations)",
            current is StandaloneToolPermissionMode.AutoAll);
        WritePermissionModeOption(terminal, 4, "Cancel", isCurrent: false);

        for (var attempt = 0; attempt < maxInputAttempts; attempt++)
        {
            terminal.Write("Select [1/2/3/4]: ", CliTextStyle.Prompt);
            var input = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            switch (input.Trim())
            {
                case "1":
                    return StandaloneToolPermissionMode.AskAll;
                case "2":
                    return StandaloneToolPermissionMode.AutoTools;
                case "3":
                    return StandaloneToolPermissionMode.AutoAll;
                case "4":
                    return null;
                default:
                    terminal.WriteLine("Enter 1, 2, 3, or 4.", CliTextStyle.Error);
                    break;
            }
        }

        return null;
    }

    internal static async Task<bool?> PromptForWorkspaceInstructionsAsync(
        ICliTerminal terminal,
        bool isEnabled,
        CancellationToken ct)
    {
        const int maxInputAttempts = 3;
        terminal.WriteLine("Workspace instructions:", CliTextStyle.Title);
        WriteWorkspaceInstructionsOption(
            terminal,
            1,
            "Disabled",
            !isEnabled);
        WriteWorkspaceInstructionsOption(
            terminal,
            2,
            "Load AGENTS.md automatically",
            isEnabled);
        WriteWorkspaceInstructionsOption(terminal, 3, "Cancel", isCurrent: false);

        for (var attempt = 0; attempt < maxInputAttempts; attempt++)
        {
            terminal.Write("Select [1/2/3]: ", CliTextStyle.Prompt);
            var input = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            switch (input.Trim())
            {
                case "1":
                    return false;
                case "2":
                    return true;
                case "3":
                    return null;
                default:
                    terminal.WriteLine("Enter 1, 2, or 3.", CliTextStyle.Error);
                    break;
            }
        }

        return null;
    }

    private static void WriteWorkspaceInstructionsOption(
        ICliTerminal terminal,
        int number,
        string description,
        bool isCurrent)
    {
        terminal.Write($"  {number}. ", CliTextStyle.Parameter);
        terminal.Write(
            description,
            isCurrent ? CliTextStyle.Selected : CliTextStyle.Default);
        terminal.WriteLine(
            isCurrent ? " (current)" : string.Empty,
            CliTextStyle.Muted);
    }

    private static async Task WriteWorkspaceInstructionsStatusAsync(
        TextWriter output,
        bool enabled,
        string workspaceRoot,
        CancellationToken ct)
    {
        if (!enabled)
        {
            await output.WriteLineAsync("Workspace instructions: disabled").ConfigureAwait(false);
            return;
        }

        var instructions = await WorkspaceInstructionsLoader.LoadAsync(workspaceRoot, ct)
            .ConfigureAwait(false);
        if (instructions is null)
        {
            await output.WriteLineAsync(
                    $"Workspace instructions: enabled ({WorkspaceInstructionsLoader.FileName} not found)")
                .ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync("Workspace instructions: enabled").ConfigureAwait(false);
        await output.WriteLineAsync($"File: {instructions.Path}").ConfigureAwait(false);
    }

    private static bool TryParseWorkspaceInstructionsMode(
        string? value,
        out bool enabled)
    {
        enabled = false;
        if (value is null)
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                return true;
            case "auto":
                enabled = true;
                return true;
            default:
                return false;
        }
    }

    private static void WritePermissionModeOption(
        ICliTerminal terminal,
        int number,
        string description,
        bool isCurrent)
    {
        terminal.Write($"  {number}. ", CliTextStyle.Parameter);
        terminal.Write(
            description,
            isCurrent ? CliTextStyle.Selected : CliTextStyle.Default);
        terminal.WriteLine(
            isCurrent ? " (current)" : string.Empty,
            CliTextStyle.Muted);
    }

    private static bool TryResolveReplWorkspace(
        string currentWorkspace,
        string argument,
        out string workspace,
        out string error)
    {
        workspace = string.Empty;
        error = string.Empty;
        var requestedPath = argument.Trim();
        if (requestedPath.Length >= 2
            && ((requestedPath[0] == '"' && requestedPath[^1] == '"')
                || (requestedPath[0] == '\'' && requestedPath[^1] == '\'')))
        {
            requestedPath = requestedPath[1..^1].Trim();
        }

        if (requestedPath.Length == 0)
        {
            error = "用法：/cd <path>";
            return false;
        }

        try
        {
            if (requestedPath == "~"
                || requestedPath.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || requestedPath.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
                requestedPath = requestedPath.Length == 1
                    ? userHome
                    : Path.Join(userHome, requestedPath[2..]);
            }

            workspace = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(requestedPath, currentWorkspace));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"工作区路径无效：{ex.Message}";
            return false;
        }

        if (!Directory.Exists(workspace))
        {
            error = $"工作区目录不存在：{workspace}";
            workspace = string.Empty;
            return false;
        }

        return true;
    }

    private static bool ReplPathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static async Task HandleSkillReplCommandAsync(
        LocalRuntime runtime,
        TextWriter output,
        TextWriter error,
        string? argument,
        CancellationToken ct)
    {
        if (argument is null)
        {
            await error.WriteLineAsync("Usage: /skill <list|show <name>|refresh>")
                .ConfigureAwait(false);
            return;
        }

        var separator = argument.IndexOf(' ');
        var subcommand = separator < 0 ? argument : argument[..separator];
        var value = separator < 0 ? null : argument[(separator + 1)..].Trim();
        if (value is { Length: 0 })
        {
            value = null;
        }

        switch (subcommand.ToUpperInvariant())
        {
            case "LIST" when value is null:
                var summary = runtime.GetSkillCatalogSummary();
                await output.WriteLineAsync(
                        $"Catalog {summary.CatalogVersion}: {summary.SkillCount} Skill(s).")
                    .ConfigureAwait(false);
                foreach (var catalogSkill in summary.Skills)
                {
                    await output.WriteLineAsync(
                            $"{catalogSkill.Name}\t{catalogSkill.SourceId}\t{catalogSkill.Description}")
                        .ConfigureAwait(false);
                }

                return;

            case "SHOW" when value is not null:
                var catalog = runtime.GetSkillCatalogSummary();
                var skill = catalog.Skills.FirstOrDefault(candidate =>
                    candidate.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (skill is null)
                {
                    await error.WriteLineAsync($"Skill '{value}' was not found.")
                        .ConfigureAwait(false);
                    return;
                }

                var source = catalog.Sources.FirstOrDefault(candidate =>
                    candidate.SourceId.Equals(skill.SourceId, StringComparison.Ordinal));
                await output.WriteLineAsync($"Name: {skill.Name}").ConfigureAwait(false);
                await output.WriteLineAsync($"Description: {skill.Description}").ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"Source: {skill.SourceId} ({source?.Location ?? "unknown"})")
                    .ConfigureAwait(false);
                await output.WriteLineAsync($"Catalog: {catalog.CatalogVersion}").ConfigureAwait(false);
                await output.WriteLineAsync($"Content hash: {skill.ContentHash}").ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"Resources: {(skill.Resources.Length == 0 ? "none" : string.Join(", ", skill.Resources))}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"Scripts: {(skill.Scripts.Length == 0 ? "none" : string.Join(", ", skill.Scripts))}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Definition:").ConfigureAwait(false);
                await output.WriteLineAsync(await runtime.LoadSkillAsync(skill.Name, ct).ConfigureAwait(false))
                    .ConfigureAwait(false);
                return;

            case "REFRESH" when value is null:
                var refreshed = await runtime.RefreshSkillCatalogAsync(ct).ConfigureAwait(false);
                await output.WriteLineAsync(
                        $"Catalog {refreshed.CatalogVersion}: {refreshed.SkillCount} Skill(s).")
                    .ConfigureAwait(false);
                return;

            default:
                await error.WriteLineAsync("Usage: /skill <list|show <name>|refresh>")
                    .ConfigureAwait(false);
                return;
        }
    }

    private static async Task<ReplCommandResult> UpdateReplSelectionAsync(
        LocalRuntime runtime,
        ICliTerminal terminal,
        string? sessionId,
        RuntimeMode mode,
        StandaloneSelection selection,
        int selectionVersion,
        IReadOnlyList<AgentConfig> agents,
        StandaloneConfig config,
        CancellationToken ct)
    {
        var nextVersion = selectionVersion;
        if (sessionId is not null)
        {
            nextVersion = checked(selectionVersion + 1);
            await runtime.UpdateSessionSelectionAsync(
                    sessionId,
                    selectionVersion,
                    BuildNextTurnSelection(
                        mode,
                        selection,
                        config,
                        agents,
                        nextVersion),
                    ct)
                .ConfigureAwait(false);
        }

        terminal.Write("Next turn: ", CliTextStyle.Muted);
        terminal.Write(
            $"{selection.Provider.Name}/{selection.Model}",
            CliTextStyle.Selected);
        terminal.Write(" (");
        terminal.Write(selection.Agent.Id, CliTextStyle.Selected);
        terminal.WriteLine(")");
        return new ReplCommandResult(sessionId, selection, nextVersion, ExitCode: null);
    }

    private static string? GetPrimaryAgentId(ModeOptions modeOptions) => modeOptions switch
    {
        ExpertModeOptions expert => expert.Agent.AgentId,
        _ => null
    };

    private static async Task<bool> TryHandleMemoryReplCommandAsync(
        string command,
        ICliTerminal terminal,
        TextWriter output,
        MemoryFileService memoryFiles,
        CancellationToken ct)
    {
        var parts = command.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        try
        {
            if (parts[0].Equals("/memory", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length > 2
                    || !TryParseMemoryScope(
                        parts.Length == 1 ? "effective" : parts[1],
                        allowEffective: true,
                        out var scope))
                {
                    output.WriteLine("Usage: /memory [global|project|effective]");
                    return true;
                }

                if (scope == MemoryScope.Effective)
                {
                    output.WriteLine($"Global path: {memoryFiles.GlobalPath}");
                    output.WriteLine($"Project path: {memoryFiles.ProjectPath}");
                }
                else
                {
                    output.WriteLine($"Path: {GetMemoryPath(memoryFiles, scope)}");
                }

                WriteMemoryContent(
                    output,
                    await memoryFiles.ReadAsync(scope, ct).ConfigureAwait(false));
                return true;
            }

            if (!parts[0].Equals("/remember", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (parts.Length != 2
                || !TryParseMemoryScope(parts[1], allowEffective: false, out var appendScope))
            {
                output.WriteLine("Usage: /remember <global|project>");
                return true;
            }

            terminal.Write("Memory item: ");
            var item = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(item))
            {
                output.WriteLine("Memory was not added.");
                return true;
            }

            await memoryFiles.AppendAsync(appendScope, item, ct).ConfigureAwait(false);
            output.WriteLine($"Updated memory: {GetMemoryPath(memoryFiles, appendScope)}");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            output.WriteLine($"Memory command failed: {ex.Message}");
            return true;
        }
    }

    private static bool TryParseMemoryScope(
        string value,
        bool allowEffective,
        out MemoryScope scope)
    {
        if (value.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Global;
            return true;
        }

        if (value.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Project;
            return true;
        }

        if (allowEffective
            && value.Equals("effective", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Effective;
            return true;
        }

        scope = default;
        return false;
    }

    private static string GetMemoryPath(
        MemoryFileService memoryFiles,
        MemoryScope scope) => scope switch
        {
            MemoryScope.Global => memoryFiles.GlobalPath,
            MemoryScope.Project => memoryFiles.ProjectPath
                ?? throw new InvalidOperationException("Project memory requires a workspace root."),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };

    private static void WriteMemoryContent(TextWriter output, string? content)
    {
        if (content is null)
        {
            output.WriteLine("(not found)");
            return;
        }

        if (content.Length == 0)
        {
            output.WriteLine("(empty)");
            return;
        }

        output.Write(content);
        if (!content.EndsWith('\n'))
        {
            output.WriteLine();
        }
    }

    private static async Task<RunOutputResult> ExecuteOneRunAsync(
        IAsyncEnumerable<RuntimeEventEnvelope> events,
        RunOutputWriter output,
        CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in events.WithCancellation(ct).ConfigureAwait(false))
            {
                var result = await output.WriteEventAsync(envelope, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await output.WriteExceptionAsync(
                    new OperationCanceledException(ct),
                    ExitCodes.UserInterrupted,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await output.WriteExceptionAsync(ex, ExitCodes.GeneralError, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return await output.WriteUnexpectedEndAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static NewSessionRunRequest BuildNewRunRequest(
        string userInput,
        RuntimeMode mode,
        StandaloneSelection selection,
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        int selectionVersion = 1,
        ExpertRequestControl? requestControl = null)
    {
        var nextTurn = BuildNextTurnSelection(
            mode,
            selection,
            config,
            agents,
            selectionVersion,
            requestControl);
        return new NewSessionRunRequest(
            SessionIdempotencyKey: Guid.NewGuid().ToString("N"),
            RunIdempotencyKey: Guid.NewGuid().ToString("N"),
            mode,
            nextTurn,
            [new TextContentBlock(userInput)]);
    }

    private static ExistingSessionRunRequest BuildExistingRunRequest(
        string sessionId,
        string userInput,
        RuntimeMode mode,
        StandaloneSelection selection,
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        int selectionVersion = 1,
        ExpertRequestControl? requestControl = null) =>
        new(
            sessionId,
            Guid.NewGuid().ToString("N"),
            BuildNextTurnSelection(mode, selection, config, agents, selectionVersion, requestControl),
            [new TextContentBlock(userInput)]);

    private static NextTurnSelection BuildNextTurnSelection(
        RuntimeMode mode,
        StandaloneSelection selection,
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        int selectionVersion = 1,
        ExpertRequestControl? requestControl = null)
    {
        var selectedAgent = BuildAgentRef(selection);
        var agentRefs = agents
            .Select(agent => ResolveAgentSelection(config, agents, agent))
            .Select(BuildAgentRef)
            .ToArray();
        ModeOptions modeOptions = mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(
                selectedAgent,
                agentRefs,
                requestControl: requestControl),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        return new NextTurnSelection(
            selectionVersion,
            mode,
            new DefaultSelection(selection.Provider.Name, selection.Model),
            modeOptions);
    }

    private static StandaloneSelection ResolveAgentSelection(
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        AgentConfig agent)
    {
        if (StandaloneConfigurationResolver.TryResolveGlobalSelection(
                config,
                agents,
                providerOverride: null,
                modelOverride: null,
                agent.Id,
                out var selection))
        {
            return selection;
        }

        throw new InvalidOperationException(
            $"Agent '{agent.Id}' has no valid Provider and model selection.");
    }

    private static AgentRef BuildAgentRef(StandaloneSelection selection) =>
        new(
            selection.Agent.Id,
            PromptTemplateVersion: "1.0",
            selection.Agent.SystemPrompt,
            ProviderId: selection.Provider.Name,
            ModelId: selection.Model,
            Description: selection.Agent.Description,
            Capabilities: selection.Agent.Capabilities);

    /// <summary>
    /// CLI Meeting defaults follow the product plan: HostDriven host, FinalOnly summarizer,
    /// participants are non-host/non-summarizer agents (at least two when available).
    /// </summary>

    private static bool TryResolveMode(string? value, out RuntimeMode mode)
    {
        switch (value?.ToUpperInvariant())
        {
            case null:
            case "":
            case "EXPERT":
            case "STANDALONE":
                mode = RuntimeMode.Expert;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static bool TryResolveRunOutputFormat(
        string? value,
        bool jsonRequested,
        out RunOutputFormat format,
        out string error)
    {
        format = jsonRequested ? RunOutputFormat.Json : RunOutputFormat.Text;
        error = string.Empty;
        if (value is null)
        {
            return true;
        }

        switch (value.ToUpperInvariant())
        {
            case "TEXT":
                format = RunOutputFormat.Text;
                break;
            case "JSON":
                format = RunOutputFormat.Json;
                break;
            case "JSONL":
                format = RunOutputFormat.JsonLines;
                break;
            default:
                error = "--output-format must be text, json, or jsonl.";
                return false;
        }

        if (jsonRequested && format is not RunOutputFormat.Json)
        {
            format = RunOutputFormat.Json;
            error = "--json cannot be combined with a non-JSON --output-format value.";
            return false;
        }

        return true;
    }

    private static async Task<string> ReadRunInputFileAsync(
        string path,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(path), ct).ConfigureAwait(false);
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return StrictUtf8.GetString(bytes.AsSpan(offset));
    }

    private static bool IsRunInputException(Exception exception) =>
        exception is ArgumentException
            or DecoderFallbackException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException;

    private static bool IsRunOutputException(Exception exception) =>
        exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException;

    private static bool TryResolveRuntimeLogLevel(
        string? configuredLevel,
        out RuntimeDiagnosticLogLevel level)
    {
        var value = string.IsNullOrWhiteSpace(configuredLevel)
            ? Environment.GetEnvironmentVariable("NETOR_AI_LOG_LEVEL")
            : configuredLevel;
        if (string.IsNullOrWhiteSpace(value))
        {
            level = RuntimeDiagnosticLogLevel.Information;
            return true;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            case "DEBUG":
                level = RuntimeDiagnosticLogLevel.Debug;
                return true;
            case "INFORMATION":
                level = RuntimeDiagnosticLogLevel.Information;
                return true;
            case "WARNING":
                level = RuntimeDiagnosticLogLevel.Warning;
                return true;
            case "ERROR":
                level = RuntimeDiagnosticLogLevel.Error;
                return true;
            default:
                level = default;
                return false;
        }
    }

    private sealed record ReplCommandResult(
        string? SessionId,
        StandaloneSelection Selection,
        int SelectionVersion,
        int? ExitCode,
        string? WorkspaceRoot = null,
        ReplHelpLanguage HelpLanguage = ReplHelpLanguage.Chinese);

    private sealed record ReplLoopResult(
        int ExitCode,
        string? WorkspaceRoot,
        StandaloneSelection Selection);

    private enum ReplInterruptState
    {
        Idle,
        Running,
        Pausing,
        Cancelling,
        Exiting
    }

    private static bool IsConfigurationException(Exception exception) =>
        exception is ArgumentException
            or ConfigBusyException
            or FileNotFoundException
            or InvalidOperationException
            or IOException
            or JsonException
            or NotSupportedException
            or StandaloneConfigurationException
            or UnauthorizedAccessException;

    private static string RedactSecrets(string message, StandaloneConfig? config)
    {
        if (config is null)
        {
            return message;
        }

        foreach (var apiKey in config.Providers
                     .Select(static provider => provider.ApiKey)
                     .Where(static apiKey => !string.IsNullOrEmpty(apiKey))
                     .Distinct(StringComparer.Ordinal))
        {
            message = message.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        return message;
    }

    private static void WriteSuccess(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", command);
                writer.WriteString("status", "ok");
                writer.WriteEndObject();
            });
        }
    }

    private static void WriteError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
        string message)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine(message);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString("status", "error");
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
    }

    private static void CancelNoThrow(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel(throwOnFirstException: false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException exception)
        {
            Trace.TraceWarning(
                "Shutdown cancellation callback failure type={0} diagnosticId={1}",
                exception.GetType().FullName,
                $"diag_cancel_{Guid.NewGuid():N}");
        }
    }
}
