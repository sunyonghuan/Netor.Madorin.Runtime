using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Cli.Tools;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneWelcomeTests
{
    private const string ApiKey = "welcome-secret-key";
    private const string BaseUrl = "https://welcome-secret.invalid/v1";

    private readonly TestContext _testContext;

    public StandaloneWelcomeTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void ReplWelcomeWriter_EmptyAgentName_WritesStableAgentId()
    {
        var terminal = new RecordingTerminal();
        var snapshot = new ReplWelcomeSnapshot(
            "0.1.0",
            RuntimeMode.Expert,
            "stable-agent-id",
            string.Empty,
            "fake",
            "fake-model",
            Path.GetFullPath("workspace"));

        ReplWelcomeWriter.Write(terminal, snapshot);

        Assert.Contains(
            $"Agent      stable-agent-id{Environment.NewLine}",
            terminal.Output.ToString(),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReplWelcomeAndHelpWriters_AssignSemanticStyles()
    {
        var terminal = new RecordingTerminal();
        ReplWelcomeWriter.Write(
            terminal,
            new ReplWelcomeSnapshot(
                "0.1.0",
                RuntimeMode.Expert,
                "agent-one",
                "Agent One",
                "provider-one",
                "model-one",
                Path.GetFullPath("workspace")));
        var wroteHelp = ReplHelpWriter.Write(
            terminal,
            "provider",
            ReplHelpLanguage.Chinese,
            out _);

        Assert.IsTrue(wroteHelp);
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == ReplWelcomeWriter.Banner
            && write.Style == CliTextStyle.Title));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "0.1.0"
            && write.Style == CliTextStyle.Title));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "/help"
            && write.Style == CliTextStyle.Command));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "/provider"
            && write.Style == CliTextStyle.Command));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "[供应商名称]"
            && write.Style == CliTextStyle.Parameter));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "不写名称时从已添加的供应商里选择。这里不能添加新供应商，添加请看 madorin config edit。"
            && write.Style == CliTextStyle.Muted));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "ready"
            && write.Style == CliTextStyle.Success));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "provider-one"
            && write.Style == CliTextStyle.Selected));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value.EndsWith("|____|", StringComparison.Ordinal)
            && write.Style == CliTextStyle.Muted));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value.Contains("有史以来", StringComparison.Ordinal)
            && write.Style == CliTextStyle.Muted));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value.Contains("service@madorins.com", StringComparison.Ordinal)
            && write.Style == CliTextStyle.Muted));
        Assert.DoesNotContain("Mode       ", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReplHelpWriter_ListsEveryCommandWithChineseDescriptions()
    {
        var terminal = new RecordingTerminal();

        var wroteHelp = ReplHelpWriter.Write(
            terminal,
            null,
            ReplHelpLanguage.Chinese,
            out var language);

        Assert.IsTrue(wroteHelp);
        Assert.AreEqual(ReplHelpLanguage.Chinese, language);
        var output = terminal.Output.ToString();
        Assert.Contains("交互命令", output, StringComparison.Ordinal);
        Assert.Contains("[] 可以不写，<> 必须写。", output, StringComparison.Ordinal);
        string[] commands =
        [
            "/help [命令]",
            "/help en",
            "/exit",
            "/provider [供应商名称]",
            "/model [模型编号]",
            "madorin config edit",
            "/agent [智能体编号]",
            "/session",
            "/new",
            "/resume [会话编号]",
            "/history [条数 | all]",
            "/export [文件路径]",
            "/compact",
            "/context",
            "/cd [目录]",
            "/status",
            "/mode",
            "/clear",
            "/memory [global | project | effective]",
            "/remember <global | project>",
            "/tools",
            "/permission",
            "/instructions",
            "/skill <list | show 名称 | refresh>"
        ];
        foreach (var command in commands)
        {
            Assert.Contains(command, output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("/pause", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/interrupt", output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PermissionModePrompt_UsesNumberedEnglishMenu()
    {
        var terminal = new RecordingTerminal("2");

        var selected = await ServiceCommands.PromptForPermissionModeAsync(
            terminal,
            StandaloneToolPermissionMode.AskAll,
            _testContext.CancellationToken);

        Assert.AreEqual(StandaloneToolPermissionMode.AutoTools, selected);
        var output = terminal.Output.ToString();
        Assert.Contains("Permission mode:", output, StringComparison.Ordinal);
        Assert.Contains("1. Ask every time (current)", output, StringComparison.Ordinal);
        Assert.Contains(
            "2. Auto-approve tools (workflow approvals still ask)",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "3. Auto-approve everything (no confirmations)",
            output,
            StringComparison.Ordinal);
        Assert.Contains("4. Cancel", output, StringComparison.Ordinal);
        Assert.Contains("Select [1/2/3/4]:", output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WorkspaceInstructionsPrompt_UsesNumberedEnglishMenu()
    {
        var terminal = new RecordingTerminal("2");

        var selected = await ServiceCommands.PromptForWorkspaceInstructionsAsync(
            terminal,
            isEnabled: false,
            _testContext.CancellationToken);

        Assert.IsTrue(selected);
        var output = terminal.Output.ToString();
        Assert.Contains("Workspace instructions:", output, StringComparison.Ordinal);
        Assert.Contains("1. Disabled (current)", output, StringComparison.Ordinal);
        Assert.Contains("2. Load AGENTS.md automatically", output, StringComparison.Ordinal);
        Assert.Contains("3. Cancel", output, StringComparison.Ordinal);
        Assert.Contains("Select [1/2/3]:", output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WorkspaceInstructionsPrompt_Cancel_ReturnsNull()
    {
        var terminal = new RecordingTerminal("3");

        var selected = await ServiceCommands.PromptForWorkspaceInstructionsAsync(
            terminal,
            isEnabled: true,
            _testContext.CancellationToken);

        Assert.IsNull(selected);
    }

    [TestMethod]
    [DataRow(CliTextStyle.Title, ConsoleColor.DarkCyan)]
    [DataRow(CliTextStyle.Command, ConsoleColor.DarkCyan)]
    [DataRow(CliTextStyle.Prompt, ConsoleColor.DarkCyan)]
    [DataRow(CliTextStyle.Parameter, ConsoleColor.Gray)]
    [DataRow(CliTextStyle.Selected, ConsoleColor.White)]
    [DataRow(CliTextStyle.Success, ConsoleColor.DarkGreen)]
    [DataRow(CliTextStyle.Warning, ConsoleColor.DarkYellow)]
    [DataRow(CliTextStyle.Error, ConsoleColor.DarkRed)]
    [DataRow(CliTextStyle.Muted, ConsoleColor.DarkGray)]
    [DataRow(CliTextStyle.Horse, ConsoleColor.White)]
    [DataRow(CliTextStyle.Accent, ConsoleColor.Yellow)]
    [DataRow(CliTextStyle.User, ConsoleColor.Magenta)]
    [DataRow(CliTextStyle.Assistant, ConsoleColor.Green)]
    public void CliTerminal_ResolveColor_UsesRestrainedPalette(
        CliTextStyle style,
        ConsoleColor expected)
    {
        Assert.AreEqual(expected, CliTerminal.ResolveColor(style));
    }

    [TestMethod]
    public async Task ReplSelectionMenu_CurrentMarker_UsesMutedStyle()
    {
        var terminal = new RecordingTerminal();

        await ReplSelectionMenu.PromptAsync(
            terminal,
            "Available providers:",
            "provider",
            [("provider-one", "Provider One"), ("provider-two", "Provider Two")],
            "provider-one",
            CancellationToken.None);

        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "provider-one"
            && write.Style == CliTextStyle.Selected));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == " (current)"
            && write.Style == CliTextStyle.Muted));
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_NoArguments_WritesSafeWelcomeBeforeFirstPromptOnce()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(
                root,
                "workspace with a deliberately long path",
                "nested segment for natural terminal wrapping");
            Directory.CreateDirectory(workspace);
            await WriteConfigurationAsync(configDirectory);
            var output = new StringWriter();

            var exitCode = RunWithWorkingDirectory(
                workspace,
                [],
                output,
                new StringReader($"/exit{Environment.NewLine}"),
                configDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            var text = output.ToString();
            var expectedVersion = ReadVersionFromCommand(configDirectory);
            var runtimeContext = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);

            Assert.Contains(ReplWelcomeWriter.Banner, text, StringComparison.Ordinal);
            Assert.Contains(expectedVersion, text, StringComparison.Ordinal);
            Assert.Contains("ready", text, StringComparison.Ordinal);
            Assert.Contains("|____|", text, StringComparison.Ordinal);
            Assert.Contains("有史以来", text, StringComparison.Ordinal);
            Assert.Contains("功能最强大 · 性能最快 · 效率最高", text, StringComparison.Ordinal);
            Assert.Contains("集成最简单 · token 最省", text, StringComparison.Ordinal);
            Assert.Contains("的 CLI", text, StringComparison.Ordinal);
            Assert.Contains("@madorins.com", text, StringComparison.Ordinal);
            Assert.Contains("service@madorins.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain($"Mode       Expert{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Agent      Friendly Agent{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Provider   fake{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Model      fake-model{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Workspace  {workspace}{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Session    New{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains(
                "Type /help to view commands, or /exit to quit.",
                text,
                StringComparison.Ordinal);
            Assert.AreEqual(1, CountOccurrences(text, ReplWelcomeWriter.Banner));

            var welcomeIndex = text.IndexOf(ReplWelcomeWriter.Banner, StringComparison.Ordinal);
            var promptIndex = text.IndexOf($"madorin [fake/fake-model]{Environment.NewLine}", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, welcomeIndex);
            Assert.IsGreaterThan(welcomeIndex, promptIndex);

            Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(BaseUrl, text, StringComparison.Ordinal);
            Assert.DoesNotContain(configDirectory, text, StringComparison.Ordinal);
            Assert.DoesNotContain(runtimeContext.DataDirectory, text, StringComparison.Ordinal);
            Assert.DoesNotContain(runtimeContext.WorkspaceKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain("RuntimeInstanceId", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_FirstUseWizard_WritesWelcomeAfterConfiguration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var output = new StringWriter();
            var input = new StringReader(string.Join(
                Environment.NewLine,
                [
                    "",
                    "fake",
                    "",
                    ApiKey,
                    "fake-model",
                    "",
                    "Wizard prompt",
                    "/exit",
                    ""
                ]));

            var exitCode = RunWithWorkingDirectory(
                workspace,
                [],
                output,
                input,
                configDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            var text = output.ToString();
            var wizardIndex = text.IndexOf(
                "Madorin standalone configuration",
                StringComparison.Ordinal);
            var welcomeIndex = text.IndexOf(ReplWelcomeWriter.Banner, StringComparison.Ordinal);
            var promptIndex = text.IndexOf($"madorin [fake/fake-model]{Environment.NewLine}", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, wizardIndex);
            Assert.IsGreaterThan(wizardIndex, welcomeIndex);
            Assert.IsGreaterThan(welcomeIndex, promptIndex);
            Assert.AreEqual(1, CountOccurrences(text, ReplWelcomeWriter.Banner));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_ExplicitRun_DoesNotWriteWelcome()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory);
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                new StringReader($"/exit{Environment.NewLine}"),
                configDirectory,
                _ => _ => new StubProvider());

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.Contains($"madorin [fake/fake-model]{Environment.NewLine}", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ReplWelcomeWriter.Banner, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("有史以来", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_JsonlSingleRun_DoesNotPolluteMachineOutput()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory);
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--input", "hello",
                    "--output-format", "jsonl"
                ],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => new StubProvider());

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            var lines = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            Assert.IsNotEmpty(lines);
            Assert.IsTrue(lines.All(static line => line.StartsWith('{')));
            Assert.DoesNotContain(ReplWelcomeWriter.Banner, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("service@madorins.com", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_InstructionsModeAuto_AppendsWorkspaceInstructions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory);
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "agents.md"),
                "# Workspace rule",
                _testContext.CancellationToken);
            var provider = new CapturingProvider();
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--input", "hello",
                    "--instructions-mode", "auto"
                ],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            var instructions = GetSystemInstructions(Assert.ContainsSingle(provider.Requests));
            Assert.Contains("## Workspace Instructions", instructions, StringComparison.Ordinal);
            Assert.Contains("# Workspace rule", instructions, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_InstructionsModeDefault_DoesNotAppendWorkspaceInstructions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory);
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "AGENTS.md"),
                "# Workspace rule",
                _testContext.CancellationToken);
            var provider = new CapturingProvider();
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace, "--input", "hello"],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            var instructions = GetSystemInstructions(Assert.ContainsSingle(provider.Requests));
            Assert.DoesNotContain("## Workspace Instructions", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("# Workspace rule", instructions, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_ReplInstructionsCommand_EnablesLoadingForLaterTurns()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory);
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Agents.Md"),
                "# Repl rule",
                _testContext.CancellationToken);
            var provider = new CapturingProvider();
            var output = new StringWriter();
            var input = new StringReader(
                $"/instructions{Environment.NewLine}2{Environment.NewLine}hello{Environment.NewLine}/exit{Environment.NewLine}");

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                input,
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.Contains("Workspace instructions: enabled", output.ToString(), StringComparison.Ordinal);
            var instructions = GetSystemInstructions(Assert.ContainsSingle(provider.Requests));
            Assert.Contains("# Repl rule", instructions, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_InvalidInstructionsMode_IsRejected()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory);
            Directory.CreateDirectory(workspace);
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--input", "hello",
                    "--instructions-mode", "yes"
                ],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => new StubProvider());

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode);
            Assert.Contains(
                "--instructions-mode must be off or auto.",
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static int RunWithWorkingDirectory(
        string workspace,
        string[] args,
        TextWriter output,
        TextReader input,
        string configDirectory)
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(workspace);
            return CliApplication.RunForTests(
                args,
                output,
                input,
                configDirectory,
                _ => _ => new StubProvider());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    private static string ReadVersionFromCommand(string configDirectory)
    {
        var output = new StringWriter();
        var exitCode = CliApplication.RunForTests(
            ["version"],
            output,
            new StringReader(string.Empty),
            configDirectory,
            _ => _ => new StubProvider());
        Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
        var line = output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries)[0];
        const string prefix = "madorin ";
        const string suffix = " (protocol ";
        var suffixIndex = line.IndexOf(suffix, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(prefix.Length, suffixIndex);
        return line[prefix.Length..suffixIndex];
    }

    private async Task WriteConfigurationAsync(string configDirectory)
    {
        var loader = new StandaloneConfigLoader(configDirectory);
        await loader.SaveAsync(
            new StandaloneConfig
            {
                DefaultProvider = "fake",
                DefaultModel = "fake-model",
                DefaultAgent = "default",
                Providers =
                [
                    new ProviderEntry
                    {
                        Name = "fake",
                        Protocol = "OpenAI",
                        BaseUrl = BaseUrl,
                        ApiKey = ApiKey,
                        Models = ["fake-model"]
                    }
                ]
            },
            _testContext.CancellationToken);
        await loader.SaveAgentAsync(
            new AgentConfig
            {
                Id = "default",
                Name = "Friendly Agent",
                SystemPrompt = "Test system prompt"
            },
            _testContext.CancellationToken);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(fragment, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += fragment.Length;
        }

        return count;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-welcome-tests",
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

    private static string GetSystemInstructions(RuntimeProviderRequest request) => string.Join(
        '\n',
        request.Messages
            .Where(static message => message.Role == RuntimeProviderRoles.System)
            .SelectMany(static message => message.Content)
            .OfType<TextContentBlock>()
            .Select(static block => block.Text));

    private sealed class StubProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "reply");
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

    private sealed class CapturingProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake model", ContextWindow: 8192)];

        public List<RuntimeProviderRequest> Requests { get; } = [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "reply");
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

    private sealed class RecordingTerminal : ICliTerminal
    {
        private readonly Queue<string?> _reads = new();

        public RecordingTerminal(params string?[] reads)
        {
            foreach (var read in reads)
            {
                _reads.Enqueue(read);
            }
        }

        public StringWriter Output { get; } = new();

        public List<(string Value, CliTextStyle Style)> Writes { get; } = [];

        public void Write(string value) => Write(value, CliTextStyle.Default);

        public void Write(string value, CliTextStyle style)
        {
            Writes.Add((value, style));
            Output.Write(value);
        }

        public void WriteLine(string value = "") => WriteLine(value, CliTextStyle.Default);

        public void WriteLine(string value, CliTextStyle style)
        {
            Writes.Add((value, style));
            Output.WriteLine(value);
        }

        public void Clear()
        {
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _reads.TryDequeue(out var value) ? value : null);
        }

        public ValueTask<string> ReadSecretAsync(
            string prompt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(string.Empty);
        }
    }
}
