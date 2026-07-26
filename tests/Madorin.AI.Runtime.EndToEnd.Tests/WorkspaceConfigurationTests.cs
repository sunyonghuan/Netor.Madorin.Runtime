using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class WorkspaceConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LoadAsync_WhenFileIsMissing_ReturnsNullWithoutCreatingDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var loader = new WorkspaceConfigLoader(workspace);

            var config = await loader.LoadAsync(TestContext.CancellationToken);

            Assert.IsNull(config);
            Assert.IsFalse(Directory.Exists(Path.Combine(workspace, ".madorin")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Constructor_WhenWorkspaceIsFileSystemRoot_UsesRootConfigurationPath()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));

        Assert.IsNotNull(root);
        var loader = new WorkspaceConfigLoader(root);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(root, ".madorin", "config.json")),
            loader.ConfigPath);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"schemaVersion\":null,\"selection\":null}")]
    [DataRow("{\"schemaVersion\":1,\"selection\":{}}")]
    public async Task LoadAsync_WithInheritanceOnlyDocument_ReturnsValidConfiguration(string json)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "工作区 with spaces");
            var loader = await WriteWorkspaceConfigAsync(workspace, json);

            var config = await loader.LoadAsync(TestContext.CancellationToken);

            if (json == "null")
            {
                Assert.IsNull(config);
            }
            else
            {
                Assert.IsNotNull(config);
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":2}")]
    [DataRow("{\"selection\":{\"provider\":\" \"}}")]
    [DataRow("{\"selection\":{\"model\":\"\t\"}}")]
    [DataRow("{\"selection\":{\"agent\":\"\r\n\"}}")]
    [DataRow("{\"providers\":[]}")]
    [DataRow("{\"selection\":{\"apiKey\":\"super-secret\"}}")]
    [DataRow("{\"selection\":")]
    public async Task LoadAsync_WithInvalidDocument_ReportsPathWithoutEchoingBody(string json)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = await WriteWorkspaceConfigAsync(Path.Combine(root, "workspace"), json);

            var exception = await Assert.ThrowsAsync<StandaloneConfigurationException>(
                () => loader.LoadAsync(TestContext.CancellationToken));

            Assert.AreEqual(
                StandaloneConfigurationResolver.WorkspaceConfigurationInvalidCode,
                exception.Code);
            Assert.Contains(loader.ConfigPath, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(json, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WhenFileExceedsLimit_RejectsBeforeDeserialization()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var loader = await WriteWorkspaceConfigAsync(
                workspace,
                new string(' ', checked((int)WorkspaceConfigLoader.MaximumConfigBytes + 1)));

            var exception = await Assert.ThrowsAsync<StandaloneConfigurationException>(
                () => loader.LoadAsync(TestContext.CancellationToken));

            Assert.AreEqual(
                StandaloneConfigurationResolver.WorkspaceConfigurationInvalidCode,
                exception.Code);
            Assert.Contains("65536-byte limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WhileAnotherReaderIsOpen_Succeeds()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = await WriteWorkspaceConfigAsync(
                Path.Combine(root, "workspace"),
                "{\"selection\":{\"agent\":\"agent-one\"}}");
            await using var reader = new FileStream(
                loader.ConfigPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous);

            var config = await loader.LoadAsync(TestContext.CancellationToken);

            Assert.AreEqual("agent-one", config?.Selection?.Agent);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_DoesNotScanParentWorkspace()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            _ = await WriteWorkspaceConfigAsync(
                root,
                "{\"selection\":{\"agent\":\"parent-agent\"}}");
            var child = Path.Combine(root, "child");
            Directory.CreateDirectory(child);
            var loader = new WorkspaceConfigLoader(child);

            var config = await loader.LoadAsync(TestContext.CancellationToken);

            Assert.IsNull(config);
            Assert.AreEqual(
                Path.Combine(child, ".madorin", "config.json"),
                loader.ConfigPath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WorkspaceDefaultsAndAgentBindings_ReportFinalSources()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                """
                {
                  "schemaVersion": 1,
                  "selection": {
                    "provider": "workspace-provider",
                    "model": "workspace-model",
                    "agent": "bound-agent"
                  }
                }
                """);
            var resolver = new StandaloneConfigurationResolver(globalLoader);

            var snapshot = await resolver.LoadAsync(
                workspace,
                TestContext.CancellationToken);

            Assert.AreEqual("bound-agent", snapshot.EffectiveSelection.Agent.Id);
            Assert.AreEqual(ConfigurationValueSource.Workspace, snapshot.EffectiveSelection.AgentSource);
            Assert.AreEqual("agent-provider", snapshot.EffectiveSelection.Provider.Name);
            Assert.AreEqual(ConfigurationValueSource.Agent, snapshot.EffectiveSelection.ProviderSource);
            Assert.AreEqual("agent-model", snapshot.EffectiveSelection.Model);
            Assert.AreEqual(ConfigurationValueSource.Agent, snapshot.EffectiveSelection.ModelSource);
            Assert.AreEqual(globalLoader.ConfigPath, snapshot.GlobalConfigPath);
            Assert.IsTrue(snapshot.WorkspaceConfigExists);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ResolveSelection_CommandLineOverridesAgentWorkspaceAndGlobal()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"selection\":{\"provider\":\"workspace-provider\",\"model\":\"workspace-model\",\"agent\":\"bound-agent\"}}");
            var resolver = new StandaloneConfigurationResolver(globalLoader);
            var snapshot = await resolver.LoadAsync(
                workspace,
                TestContext.CancellationToken);

            var selection = StandaloneConfigurationResolver.ResolveSelection(
                snapshot,
                "workspace-provider",
                "command-model",
                "global-agent");

            Assert.AreEqual("global-agent", selection.Agent.Id);
            Assert.AreEqual("workspace-provider", selection.Provider.Name);
            Assert.AreEqual("command-model", selection.Model);
            Assert.AreEqual(ConfigurationValueSource.CommandLine, selection.AgentSource);
            Assert.AreEqual(ConfigurationValueSource.CommandLine, selection.ProviderSource);
            Assert.AreEqual(ConfigurationValueSource.CommandLine, selection.ModelSource);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WithPartialWorkspaceSelection_InheritsEachFieldIndependently()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"schemaVersion\":1,\"selection\":{\"model\":\"workspace-model\"}}");

            var snapshot = await new StandaloneConfigurationResolver(globalLoader).LoadAsync(
                workspace,
                TestContext.CancellationToken);

            Assert.AreEqual("global-agent", snapshot.EffectiveSelection.Agent.Id);
            Assert.AreEqual(ConfigurationValueSource.Global, snapshot.EffectiveSelection.AgentSource);
            Assert.AreEqual("global-provider", snapshot.EffectiveSelection.Provider.Name);
            Assert.AreEqual(ConfigurationValueSource.Global, snapshot.EffectiveSelection.ProviderSource);
            Assert.AreEqual("workspace-model", snapshot.EffectiveSelection.Model);
            Assert.AreEqual(ConfigurationValueSource.Workspace, snapshot.EffectiveSelection.ModelSource);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("{\"selection\":{\"agent\":\"missing-agent\"}}", "selection.agent")]
    [DataRow("{\"selection\":{\"provider\":\"missing-provider\"}}", "selection.provider")]
    [DataRow("{\"selection\":{\"model\":\"missing-model\"}}", "selection.model")]
    public async Task LoadAsync_WithInvalidWorkspaceReference_ReportsWorkspaceSelection(
        string json,
        string expectedField)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            var workspace = Path.Combine(root, "workspace");
            var workspaceLoader = await WriteWorkspaceConfigAsync(workspace, json);

            var exception = await Assert.ThrowsAsync<StandaloneConfigurationException>(
                () => new StandaloneConfigurationResolver(globalLoader).LoadAsync(
                    workspace,
                    TestContext.CancellationToken));

            Assert.AreEqual(
                StandaloneConfigurationResolver.WorkspaceSelectionNotFoundCode,
                exception.Code);
            Assert.Contains(workspaceLoader.ConfigPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains(expectedField, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WhenGlobalConfigIsInvalid_WorkspaceCannotMaskFailure()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            await globalLoader.UpdateConfigAsync(
                config =>
                {
                    Assert.IsNotNull(config);
                    config.DefaultModel = "missing-global-model";
                    return config;
                },
                TestContext.CancellationToken);
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"selection\":{\"model\":\"workspace-model\"}}");

            var exception = await Assert.ThrowsAsync<StandaloneConfigurationException>(
                () => new StandaloneConfigurationResolver(globalLoader).LoadAsync(
                    workspace,
                    TestContext.CancellationToken));

            Assert.AreEqual(
                StandaloneConfigurationResolver.ConfigurationInvalidCode,
                exception.Code);
            Assert.Contains(globalLoader.ConfigPath, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_ReturnsFrozenSnapshotUntilResolverRunsAgain()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"selection\":{\"model\":\"workspace-model\"}}");
            var resolver = new StandaloneConfigurationResolver(globalLoader);
            var original = await resolver.LoadAsync(workspace, TestContext.CancellationToken);

            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"selection\":{\"provider\":\"workspace-provider\",\"model\":\"command-model\"}}");
            var refreshed = await resolver.LoadAsync(workspace, TestContext.CancellationToken);

            Assert.AreEqual("global-provider", original.EffectiveSelection.Provider.Name);
            Assert.AreEqual("workspace-model", original.EffectiveSelection.Model);
            Assert.AreEqual("workspace-provider", refreshed.EffectiveSelection.Provider.Name);
            Assert.AreEqual("command-model", refreshed.EffectiveSelection.Model);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_DifferentWorkspacesShareOneGlobalCatalog()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var globalLoader = await WriteGlobalConfigurationAsync(Path.Combine(root, "global"));
            var firstWorkspace = Path.Combine(root, "workspace-one");
            var secondWorkspace = Path.Combine(root, "workspace-two");
            _ = await WriteWorkspaceConfigAsync(
                firstWorkspace,
                "{\"selection\":{\"model\":\"workspace-model\"}}");
            _ = await WriteWorkspaceConfigAsync(
                secondWorkspace,
                "{\"selection\":{\"provider\":\"workspace-provider\",\"model\":\"command-model\"}}");
            var resolver = new StandaloneConfigurationResolver(globalLoader);

            var first = await resolver.LoadAsync(firstWorkspace, TestContext.CancellationToken);
            var second = await resolver.LoadAsync(secondWorkspace, TestContext.CancellationToken);

            Assert.AreEqual(first.GlobalConfigPath, second.GlobalConfigPath);
            CollectionAssert.AreEquivalent(
                first.GlobalConfig.Providers.Select(static provider => provider.Name).ToArray(),
                second.GlobalConfig.Providers.Select(static provider => provider.Name).ToArray());
            Assert.AreNotEqual(
                first.EffectiveSelection.Provider.Name,
                second.EffectiveSelection.Provider.Name);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ConfigShow_WithWorkspace_ReportsEffectiveSourcesAndMasksCredentials()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "global");
            _ = await WriteGlobalConfigurationAsync(configDirectory);
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"selection\":{\"agent\":\"bound-agent\"}}");
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                ["config", "show", "--workspace", workspace, "--json"],
                output,
                new StringReader(string.Empty),
                configDirectory,
                CreateProviderFactory());

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.DoesNotContain("global-provider-secret", output.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(workspace, Path.GetDirectoryName(
                Path.GetDirectoryName(data.GetProperty("workspaceConfigPath").GetString())!));
            Assert.IsTrue(data.GetProperty("workspaceConfigExists").GetBoolean());
            var selection = data.GetProperty("effectiveSelection");
            Assert.AreEqual("bound-agent", selection.GetProperty("agent").GetProperty("value").GetString());
            Assert.AreEqual("workspace", selection.GetProperty("agent").GetProperty("source").GetString());
            Assert.AreEqual("agent-provider", selection.GetProperty("provider").GetProperty("value").GetString());
            Assert.AreEqual("agent", selection.GetProperty("provider").GetProperty("source").GetString());
            Assert.AreEqual("agent-model", selection.GetProperty("model").GetProperty("value").GetString());
            Assert.AreEqual("agent", selection.GetProperty("model").GetProperty("source").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ConfigValidate_WhenWorkspaceFileIsMissing_SucceedsWithGlobalFallback()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "global");
            _ = await WriteGlobalConfigurationAsync(configDirectory);
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                ["config", "validate", "--workspace", workspace, "--json"],
                output,
                new StringReader(string.Empty),
                configDirectory,
                CreateProviderFactory());

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.IsTrue(data.GetProperty("valid").GetBoolean());
            Assert.IsFalse(data.GetProperty("workspaceConfigExists").GetBoolean());
            Assert.AreEqual(
                "global",
                data.GetProperty("effectiveSelection")
                    .GetProperty("model")
                    .GetProperty("source")
                    .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task Run_WithWorkspaceSelection_UsesWorkspaceDefaults()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "global");
            _ = await WriteGlobalConfigurationAsync(configDirectory);
            var workspace = Path.Combine(root, "workspace");
            _ = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"selection\":{\"provider\":\"workspace-provider\",\"model\":\"workspace-model\"}}");
            var selections = new List<NextTurnSelection>();
            var output = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace, "--input", "workspace selection"],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => selection =>
                {
                    selections.Add(selection);
                    return new RecordingProvider(
                        selection.DefaultSelection.ProviderId,
                        selection.DefaultSelection.ModelId);
                });

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            var selection = Assert.ContainsSingle(selections);
            Assert.AreEqual("workspace-provider", selection.DefaultSelection.ProviderId);
            Assert.AreEqual("workspace-model", selection.DefaultSelection.ModelId);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task Run_WithInvalidWorkspaceJson_WritesStableMachineError()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "global");
            _ = await WriteGlobalConfigurationAsync(configDirectory);
            var workspace = Path.Combine(root, "workspace");
            var loader = await WriteWorkspaceConfigAsync(
                workspace,
                "{\"apiKey\":\"must-not-leak\"}");
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--input", "invalid",
                    "--output-format", "json"
                ],
                output,
                new StringReader(string.Empty),
                configDirectory,
                CreateProviderFactory(),
                error: error);

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode, output.ToString());
            Assert.AreEqual(string.Empty, error.ToString());
            Assert.DoesNotContain("must-not-leak", output.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
            Assert.AreEqual(
                StandaloneConfigurationResolver.WorkspaceConfigurationInvalidCode,
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(
                loader.ConfigPath,
                document.RootElement.GetProperty("error").GetProperty("message").GetString()!,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>
        CreateProviderFactory() =>
        _ => selection => new RecordingProvider(
            selection.DefaultSelection.ProviderId,
            selection.DefaultSelection.ModelId);

    private async Task<StandaloneConfigLoader> WriteGlobalConfigurationAsync(string directory)
    {
        var loader = new StandaloneConfigLoader(directory);
        await loader.SaveAsync(
            new StandaloneConfig
            {
                DefaultProvider = "global-provider",
                DefaultModel = "global-model",
                DefaultAgent = "global-agent",
                Providers =
                [
                    CreateProvider(
                        "global-provider",
                        "global-model",
                        "workspace-model",
                        "command-model"),
                    CreateProvider(
                        "workspace-provider",
                        "workspace-model",
                        "command-model"),
                    CreateProvider(
                        "agent-provider",
                        "agent-model",
                        "command-model")
                ]
            },
            TestContext.CancellationToken);
        await loader.SaveAgentAsync(
            new AgentConfig
            {
                Id = "global-agent",
                Name = "Global Agent",
                SystemPrompt = "Global prompt"
            },
            TestContext.CancellationToken);
        await loader.SaveAgentAsync(
            new AgentConfig
            {
                Id = "bound-agent",
                Name = "Bound Agent",
                SystemPrompt = "Bound prompt",
                Provider = "agent-provider",
                Model = "agent-model"
            },
            TestContext.CancellationToken);
        return loader;
    }

    private async Task<WorkspaceConfigLoader> WriteWorkspaceConfigAsync(
        string workspace,
        string json)
    {
        Directory.CreateDirectory(workspace);
        var loader = new WorkspaceConfigLoader(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(loader.ConfigPath)!);
        await File.WriteAllTextAsync(
            loader.ConfigPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.CancellationToken);
        return loader;
    }

    private static ProviderEntry CreateProvider(string name, params string[] models) =>
        new()
        {
            Name = name,
            Protocol = "OpenAI",
            BaseUrl = "https://example.invalid/v1",
            ApiKey = $"{name}-secret",
            Models = [.. models]
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-workspace-config-tests",
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

    private sealed class RecordingProvider(
        string providerId,
        string modelId) : IRuntimeProviderAdapter
    {
        public string ProviderId => providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(modelId, modelId, ContextWindow: 8192)];

        public ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "workspace-reply");
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
