using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;
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
        var wroteHelp = ReplHelpWriter.Write(terminal, "provider");

        Assert.IsTrue(wroteHelp);
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "Madorin AI Runtime 0.1.0"
            && write.Style == CliTextStyle.Title));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "/help"
            && write.Style == CliTextStyle.Command));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "/provider"
            && write.Style == CliTextStyle.Command));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "[providerId]"
            && write.Style == CliTextStyle.Parameter));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "选择或更新下一轮使用的模型服务商。"
            && write.Style == CliTextStyle.Muted));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "ready"
            && write.Style == CliTextStyle.Success));
        Assert.IsTrue(terminal.Writes.Any(write =>
            write.Value == "provider-one"
            && write.Style == CliTextStyle.Selected));
    }

    [TestMethod]
    public void ReplHelpWriter_ListsEveryCommandWithChineseDescriptions()
    {
        var terminal = new RecordingTerminal();

        var wroteHelp = ReplHelpWriter.Write(terminal, null);

        Assert.IsTrue(wroteHelp);
        (string Command, string Description)[] expectedEntries =
        [
            ("/model [modelId]", "选择或更新下一轮使用的模型。"),
            ("/provider [providerId]", "选择或更新下一轮使用的模型服务商。"),
            ("/agent [agentId]", "选择或更新下一轮使用的智能体。"),
            ("/memory [global|project|effective]", "查看记忆内容及其来源。"),
            ("/remember <global|project>", "经交互确认后追加一条记忆。"),
            ("/mode", "查看当前交互会话中不可变更的工作模式。"),
            ("/compact", "创建或刷新当前会话的上下文摘要。"),
            ("/context", "查看规范历史记录和有效上下文投影的统计信息。"),
            ("/session", "查看当前会话及下一轮选择。"),
            ("/new", "在下次提交内容时新建会话。"),
            ("/resume [sessionId]", "列出可恢复的会话，或切换到指定会话。"),
            ("/export [path]", "将当前会话导出为 Markdown 文件。"),
            ("/tools", "查看工具及其权限状态。"),
            ("/skill <list|show <name>|refresh>", "查看或刷新本地技能目录。"),
            ("/history [positive-count|all]", "查看最近的规范化消息。"),
            ("/clear", "清空当前交互式控制台，不删除历史记录。"),
            ("/cd [path]", "查看或切换当前工作区。"),
            ("/status", "查看运行时、工作区、活动、记忆和工具数量。"),
            ("/help [command]", "显示命令列表或指定命令的详细帮助。"),
            ("/exit | /quit", "保存当前会话并退出交互界面。")
        ];
        var descriptionColumn = expectedEntries.Max(entry => entry.Command.Length) + 4;
        var expectedOutput = string.Join(
            Environment.NewLine,
            expectedEntries.Select(entry =>
                $"{entry.Command.PadRight(descriptionColumn)}{entry.Description}"));

        Assert.AreEqual(
            $"可用命令：{Environment.NewLine}{expectedOutput}{Environment.NewLine}",
            terminal.Output.ToString());
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

            Assert.Contains($"Madorin AI Runtime {expectedVersion}", text, StringComparison.Ordinal);
            Assert.Contains("Local runtime ready", text, StringComparison.Ordinal);
            Assert.Contains($"Mode       Expert{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Agent      Friendly Agent{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Provider   fake{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Model      fake-model{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Workspace  {workspace}{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains($"Session    New{Environment.NewLine}", text, StringComparison.Ordinal);
            Assert.Contains(
                "Type /help to view commands, or /exit to quit.",
                text,
                StringComparison.Ordinal);
            Assert.AreEqual(1, CountOccurrences(text, "Madorin AI Runtime"));

            var welcomeIndex = text.IndexOf("Madorin AI Runtime", StringComparison.Ordinal);
            var promptIndex = text.IndexOf("madorin [fake/fake-model] > ", StringComparison.Ordinal);
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
            var welcomeIndex = text.IndexOf("Madorin AI Runtime", StringComparison.Ordinal);
            var promptIndex = text.IndexOf("madorin [fake/fake-model] > ", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, wizardIndex);
            Assert.IsGreaterThan(wizardIndex, welcomeIndex);
            Assert.IsGreaterThan(welcomeIndex, promptIndex);
            Assert.AreEqual(1, CountOccurrences(text, "Madorin AI Runtime"));
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
            Assert.Contains("madorin [fake/fake-model] > ", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Madorin AI Runtime", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Local runtime ready", output.ToString(), StringComparison.Ordinal);
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
            Assert.DoesNotContain("Madorin AI Runtime", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Local runtime ready", output.ToString(), StringComparison.Ordinal);
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

    private sealed class RecordingTerminal : ICliTerminal
    {
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
            return ValueTask.FromResult<string?>(null);
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
