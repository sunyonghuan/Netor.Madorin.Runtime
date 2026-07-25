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
        var output = new StringWriter();
        var snapshot = new ReplWelcomeSnapshot(
            "0.1.0",
            RuntimeMode.Expert,
            "stable-agent-id",
            string.Empty,
            "fake",
            "fake-model",
            Path.GetFullPath("workspace"));

        ReplWelcomeWriter.Write(output, snapshot);

        Assert.Contains(
            $"Agent      stable-agent-id{Environment.NewLine}",
            output.ToString(),
            StringComparison.Ordinal);
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
            Assert.Contains("Enter a message to begin. /exit to quit.", text, StringComparison.Ordinal);
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
}
