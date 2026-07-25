using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class StandaloneConfigurationSafetyTests
{
    private readonly TestContext _testContext;

    public StandaloneConfigurationSafetyTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void Validate_MultipleProvidersModelsAndAgents_ReturnsNoErrors()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var config = CreateConfig("model-a");
            config.Providers.Add(new ProviderEntry
            {
                Name = "anthropic",
                Protocol = "Anthropic",
                BaseUrl = "https://api.anthropic.com",
                ApiKey = "anthropic-secret",
                Models = ["model-b", "model-c"]
            });
            var documents = new List<AgentConfigDocument>
            {
                CreateAgentDocument(root, "default", "openai", "model-a"),
                CreateAgentDocument(root, "reviewer", "anthropic", "model-c")
            };

            var errors = StandaloneConfigValidator.Validate(
                config,
                Path.Combine(root, "config.json"),
                documents);

            Assert.IsEmpty(errors);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Validate_DuplicatesUnsafeIdsAndBrokenReferences_ReturnsFieldErrors()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var config = CreateConfig("model-a");
            config.DefaultProvider = "missing";
            config.DefaultModel = "missing-model";
            config.DefaultAgent = "missing-agent";
            config.Providers[0].Models = ["model-a", "model-a"];
            config.Providers.Add(new ProviderEntry
            {
                Name = "openai",
                Protocol = "Unsupported",
                BaseUrl = "not-a-url",
                ApiKey = "other-secret",
                Models = ["model-b"]
            });
            var documents = new List<AgentConfigDocument>
            {
                CreateAgentDocument(root, "duplicate", "openai", "missing-model"),
                CreateAgentDocument(root, "duplicate", "missing", "model-a"),
                new(
                    Path.Combine(root, "unsafe.json"),
                    new AgentConfig
                    {
                        Id = "../unsafe",
                        Name = "Unsafe",
                        Temperature = 3
                    })
            };

            var errors = StandaloneConfigValidator.Validate(
                config,
                Path.Combine(root, "config.json"),
                documents);
            var fields = errors.Select(static error => error.Field).ToList();

            Assert.Contains("defaultProvider", fields);
            Assert.Contains("defaultAgent", fields);
            Assert.IsTrue(fields.Any(static field => field.EndsWith(".models[1]", StringComparison.Ordinal)));
            Assert.IsTrue(fields.Any(static field => field.EndsWith(".protocol", StringComparison.Ordinal)));
            Assert.Contains("id", fields);
            Assert.Contains("temperature", fields);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Validate_NullProviderCollections_ReturnsErrorsInsteadOfThrowing()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var missingProviders = CreateConfig("model-a");
            missingProviders.Providers = null!;
            var missingProviderErrors = StandaloneConfigValidator.Validate(
                missingProviders,
                Path.Combine(root, "missing-providers.json"));

            var nullEntries = CreateConfig("model-a");
            nullEntries.Providers =
            [
                null!,
                new ProviderEntry
                {
                    Name = "empty-models",
                    Protocol = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "test-key",
                    Models = null!
                }
            ];
            var nullEntryErrors = StandaloneConfigValidator.Validate(
                nullEntries,
                Path.Combine(root, "null-entries.json"));

            Assert.IsTrue(missingProviderErrors.Any(
                static error => error.Field == "providers"));
            Assert.IsTrue(nullEntryErrors.Any(
                static error => error.Field == "providers[0]"));
            Assert.IsTrue(nullEntryErrors.Any(
                static error => error.Field == "providers[1].models"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Validate_EmptyAgentDirectory_ReportsMissingDefaultAgent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var errors = StandaloneConfigValidator.Validate(
                CreateConfig("model-a"),
                Path.Combine(root, "config.json"),
                []);

            Assert.IsTrue(errors.Any(static error => error.Field == "defaultAgent"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunEditAsync_AddProvider_PreservesExistingProvider()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(CreateConfig("model-a"), _testContext.CancellationToken);
            await SaveDefaultAgentAsync(loader);
            var terminal = new CliTerminal(
                new StringReader(string.Join(
                    Environment.NewLine,
                    ["1", "2", "2", "anthropic", "https://api.anthropic.com", "anthropic-secret", "claude-opus-4-1"])),
                new StringWriter());

            var updated = await ConfigWizard.RunEditAsync(
                root,
                terminal,
                _testContext.CancellationToken);

            Assert.HasCount(2, updated.Providers);
            var original = Assert.ContainsSingle(updated.Providers.Where(
                static provider => provider.Name == "openai"));
            Assert.AreEqual("openai-secret", original.ApiKey);
            Assert.AreEqual("https://api.openai.com/v1", original.BaseUrl);
            var added = Assert.ContainsSingle(updated.Providers.Where(
                static provider => provider.Name == "anthropic"));
            Assert.AreEqual("Anthropic", added.Protocol);
            Assert.AreEqual("anthropic-secret", added.ApiKey);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunEditAsync_EditDefaults_PreservesProviderCredentials()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            var config = CreateConfig("model-a");
            config.Providers.Add(new ProviderEntry
            {
                Name = "anthropic",
                Protocol = "Anthropic",
                BaseUrl = "https://api.anthropic.com",
                ApiKey = "anthropic-secret",
                Models = ["model-b"]
            });
            await loader.SaveAsync(config, _testContext.CancellationToken);
            await SaveDefaultAgentAsync(loader);
            var terminal = new CliTerminal(
                new StringReader(string.Join(Environment.NewLine, ["2", "2", "1", "1"])),
                new StringWriter());

            var updated = await ConfigWizard.RunEditAsync(
                root,
                terminal,
                _testContext.CancellationToken);

            Assert.AreEqual("anthropic", updated.DefaultProvider);
            Assert.AreEqual("model-b", updated.DefaultModel);
            Assert.AreEqual("openai-secret", updated.Providers[0].ApiKey);
            Assert.AreEqual("https://api.openai.com/v1", updated.Providers[0].BaseUrl);
            Assert.AreEqual("anthropic-secret", updated.Providers[1].ApiKey);
            Assert.AreEqual("https://api.anthropic.com", updated.Providers[1].BaseUrl);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunEditAsync_EditProviderWithEmptyValues_PreservesCurrentFields()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(CreateConfig("model-a"), _testContext.CancellationToken);
            await SaveDefaultAgentAsync(loader);
            var input = string.Join(
                    Environment.NewLine,
                    ["1", "1", "", "", "", "", ""])
                + Environment.NewLine;
            var terminal = new CliTerminal(new StringReader(input), new StringWriter());

            var updated = await ConfigWizard.RunEditAsync(
                root,
                terminal,
                _testContext.CancellationToken);

            var provider = Assert.ContainsSingle(updated.Providers);
            Assert.AreEqual("openai", provider.Name);
            Assert.AreEqual("OpenAI", provider.Protocol);
            Assert.AreEqual("https://api.openai.com/v1", provider.BaseUrl);
            Assert.AreEqual("openai-secret", provider.ApiKey);
            Assert.HasCount(1, provider.Models);
            Assert.AreEqual("model-a", provider.Models[0]);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunInitAsync_DiscoverModels_UsesCatalogAndDisposesTemporaryAdapter()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string apiKey = "catalog-secret-api-key";
            var output = new StringWriter();
            var terminal = new CliTerminal(
                new StringReader(string.Join(
                    Environment.NewLine,
                    [
                        "3",
                        "catalog-provider",
                        "https://catalog.example.invalid/v1",
                        apiKey,
                        "discover",
                        "2",
                        "catalog prompt"
                    ])),
                output);
            var adapter = new CatalogProvider(
                "catalog-provider",
                static ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<ProviderModel> models =
                    [
                        new("catalog-model-a", "Catalog model A"),
                        new("catalog-model-b", "Catalog model B")
                    ];
                    return ValueTask.FromResult(models);
                });

            var config = await ConfigWizard.RunInitAsync(
                root,
                terminal,
                (candidate, providerId) =>
                {
                    Assert.AreEqual("catalog-provider", providerId);
                    var provider = Assert.ContainsSingle(candidate.Providers);
                    Assert.AreEqual(apiKey, provider.ApiKey);
                    return adapter;
                },
                _testContext.CancellationToken);

            Assert.IsTrue(adapter.FetchCalled);
            Assert.IsTrue(adapter.Disposed);
            Assert.AreEqual("catalog-model-b", config.DefaultModel);
            var models = Assert.ContainsSingle(config.Providers).Models;
            Assert.HasCount(2, models);
            Assert.AreEqual("catalog-model-a", models[0]);
            Assert.AreEqual("catalog-model-b", models[1]);
            Assert.Contains("Discovered 2 model(s).", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunInitAsync_DiscoveryFailure_AllowsManualModelWithoutExposingResponse()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string responseSecret = "catalog-response-secret";
            var output = new StringWriter();
            var terminal = new CliTerminal(
                new StringReader(string.Join(
                    Environment.NewLine,
                    [
                        "3",
                        "catalog-provider",
                        "https://catalog.example.invalid/v1",
                        "catalog-key",
                        "discover",
                        "manual-model",
                        "",
                        "manual prompt"
                    ])),
                output);
            var adapter = new CatalogProvider(
                "catalog-provider",
                _ => ValueTask.FromException<IReadOnlyList<ProviderModel>>(
                    new InvalidDataException($"Invalid response {responseSecret}.")));

            var config = await ConfigWizard.RunInitAsync(
                root,
                terminal,
                (_, _) => adapter,
                _testContext.CancellationToken);

            var provider = Assert.ContainsSingle(config.Providers);
            Assert.HasCount(1, provider.Models);
            Assert.AreEqual("manual-model", provider.Models[0]);
            Assert.AreEqual("manual-model", config.DefaultModel);
            Assert.IsTrue(adapter.Disposed);
            Assert.Contains("invalid protocol response", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(responseSecret, output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunInitAsync_DiscoveryCancelled_DoesNotWriteConfiguration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var terminal = new CliTerminal(
                new StringReader(string.Join(
                    Environment.NewLine,
                    [
                        "3",
                        "catalog-provider",
                        "https://catalog.example.invalid/v1",
                        "catalog-key",
                        "discover"
                    ])),
                new StringWriter());
            var adapter = new CatalogProvider(
                "catalog-provider",
                ct =>
                {
                    cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<IReadOnlyList<ProviderModel>>([]);
                });

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                ConfigWizard.RunInitAsync(
                    root,
                    terminal,
                    (_, _) => adapter,
                    cancellation.Token));

            Assert.IsTrue(adapter.Disposed);
            Assert.IsFalse(File.Exists(new StandaloneConfigLoader(root).ConfigPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_InvalidJson_ReportsPathWithoutEchoingSecret()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            const string secret = "secret-json-value";
            await File.WriteAllTextAsync(
                loader.ConfigPath,
                $"{{\"apiKey\":\"{secret}\",",
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<JsonException>(
                () => loader.LoadAsync(_testContext.CancellationToken));

            Assert.Contains(loader.ConfigPath, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task UpdateConfigAsync_ConcurrentWriters_RereadAfterAcquiringLock()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(CreateConfig("initial"), _testContext.CancellationToken);
            using var firstEntered = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            string? secondObservedModel = null;

            var firstUpdate = Task.Run(
                () => loader.UpdateConfigAsync(
                    current =>
                    {
                        Assert.IsNotNull(current);
                        firstEntered.Set();
                        Assert.IsTrue(releaseFirst.Wait(
                            TimeSpan.FromSeconds(5),
                            _testContext.CancellationToken));
                        current.DefaultModel = "first";
                        current.Providers[0].Models = ["first", "second"];
                        return current;
                    },
                    _testContext.CancellationToken),
                _testContext.CancellationToken);
            Assert.IsTrue(firstEntered.Wait(
                TimeSpan.FromSeconds(5),
                _testContext.CancellationToken));
            var secondUpdate = Task.Run(
                () => loader.UpdateConfigAsync(
                    current =>
                    {
                        Assert.IsNotNull(current);
                        secondObservedModel = current.DefaultModel;
                        current.DefaultModel = "second";
                        return current;
                    },
                    _testContext.CancellationToken),
                _testContext.CancellationToken);

            releaseFirst.Set();
            await Task.WhenAll(firstUpdate, secondUpdate);

            Assert.AreEqual("first", secondObservedModel);
            var saved = await loader.LoadAsync(_testContext.CancellationToken);
            Assert.IsNotNull(saved);
            Assert.AreEqual("second", saved.DefaultModel);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task SaveAsync_WhenConfigLockRemainsHeld_ThrowsBusyAfterFiveSeconds()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(CreateConfig("initial"), _testContext.CancellationToken);
            await using var heldLock = new FileStream(
                loader.ConfigLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsExactlyAsync<ConfigBusyException>(
                () => loader.SaveAsync(
                    CreateConfig("updated"),
                    _testContext.CancellationToken));

            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5), stopwatch.Elapsed);
            Assert.Contains(loader.ConfigLockPath, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task SaveAsync_WhenDestinationCannotBeReplaced_PreservesOriginalFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(CreateConfig("initial"), _testContext.CancellationToken);
            var original = await File.ReadAllTextAsync(
                loader.ConfigPath,
                _testContext.CancellationToken);
            await using var heldConfig = new FileStream(
                loader.ConfigPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var exception = await Assert.ThrowsAsync<Exception>(
                () => loader.SaveAsync(
                    CreateConfig("updated"),
                    _testContext.CancellationToken));
            Assert.IsTrue(exception is IOException or UnauthorizedAccessException);

            var preserved = await File.ReadAllTextAsync(
                loader.ConfigPath,
                _testContext.CancellationToken);
            Assert.AreEqual(original, preserved);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task SaveAsync_AppliesCurrentUserOnlyPermissions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(CreateConfig("model-a"), _testContext.CancellationToken);

            if (OperatingSystem.IsWindows())
            {
                AssertWindowsPermissions(loader.ConfigPath);
            }
            else
            {
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(loader.ConfigPath));
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(loader.ConfigDirectory));
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunInitAsync_WhenRebuildConfirmed_CreatesUtcBackupAndPreservesPrompt()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(root);
            var existingConfig = CreateConfig("model-a");
            existingConfig.Providers[0].ApiVersion = "2024-10-21";
            await loader.SaveAsync(existingConfig, _testContext.CancellationToken);
            await loader.SaveAgentAsync(
                new AgentConfig
                {
                    Id = "default",
                    Name = "Default",
                    SystemPrompt = "old prompt",
                    Provider = "openai",
                    Model = "model-a"
                },
                _testContext.CancellationToken);
            const string newPrompt = "  new prompt with preserved spaces  ";
            var terminalOutput = new StringWriter();
            var terminal = new CliTerminal(
                new StringReader(string.Join(
                    Environment.NewLine,
                    ["y", "", "", "", "", "", "", newPrompt, ""])),
                terminalOutput);

            var rebuilt = await ConfigWizard.RunInitAsync(
                root,
                terminal,
                _testContext.CancellationToken);

            Assert.AreEqual("model-a", rebuilt.DefaultModel);
            Assert.AreEqual("2024-10-21", Assert.ContainsSingle(rebuilt.Providers).ApiVersion);
            var backup = Assert.ContainsSingle(
                Directory.EnumerateFiles(root, "config.*Z.bak.json"));
            Assert.MatchesRegex(
                @"config\.\d{8}T\d{9}Z\.bak\.json$",
                Path.GetFileName(backup));
            var agent = Assert.ContainsSingle(
                await loader.LoadAgentsAsync(_testContext.CancellationToken));
            Assert.AreEqual(newPrompt, agent.SystemPrompt);
            Assert.IsNull(agent.Provider);
            Assert.IsNull(agent.Model);
            Assert.Contains("Backup created:", terminalOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static StandaloneConfig CreateConfig(string model) =>
        new()
        {
            DefaultProvider = "openai",
            DefaultModel = model,
            DefaultAgent = "default",
            Providers =
            [
                new ProviderEntry
                {
                    Name = "openai",
                    Protocol = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "openai-secret",
                    Models = [model]
                }
            ]
        };

    private async Task SaveDefaultAgentAsync(StandaloneConfigLoader loader)
    {
        await loader.SaveAgentAsync(
            new AgentConfig
            {
                Id = "default",
                Name = "Default",
                SystemPrompt = "Default prompt",
                Provider = "openai",
                Model = "model-a"
            },
            _testContext.CancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsPermissions(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User;
        Assert.IsNotNull(currentUser);
        var security = new FileInfo(path).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.IsNotEmpty(rules);
        Assert.IsTrue(rules.All(rule => currentUser.Equals(rule.IdentityReference)));
    }

    private static AgentConfigDocument CreateAgentDocument(
        string root,
        string id,
        string provider,
        string model) =>
        new(
            Path.Combine(root, $"{id}.json"),
            new AgentConfig
            {
                Id = id,
                Name = id,
                SystemPrompt = $"Prompt for {id}",
                Provider = provider,
                Model = model
            });

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-config-safety-tests",
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

    private sealed class CatalogProvider(
        string providerId,
        Func<CancellationToken, ValueTask<IReadOnlyList<ProviderModel>>> fetchModels) :
        IRuntimeProviderAdapter,
        IRuntimeProviderModelCatalogSource,
        IAsyncDisposable
    {
        public string ProviderId { get; } = providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("configured-model", "Configured model")];

        public bool FetchCalled { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
            CancellationToken ct = default)
        {
            FetchCalled = true;
            Assert.IsTrue(ct.CanBeCanceled);
            return fetchModels(ct);
        }

        public IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Configuration discovery does not execute completions.");

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
