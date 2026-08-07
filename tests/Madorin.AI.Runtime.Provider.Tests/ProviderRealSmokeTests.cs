using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;
using Madorin.AI.Runtime.Tools.Abstractions;
using Netor.Anthropic.Minimal;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
[TestCategory("External")]
public sealed class ProviderRealSmokeTests
{
    [TestMethod]
    [TestCategory("Anthropic")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnthropicMinimal_NonStreaming_ReturnsTextAndUsage()
    {
        var configuration = RequireAnthropicConfiguration();
        using var client = new AnthropicMinimalClient(
            new AnthropicMinimalClientOptions
            {
                ApiKey = configuration.ApiKey,
                BaseUrl = configuration.BaseUrl,
                Timeout = TimeSpan.FromSeconds(90)
            });

        var response = await client.Messages.CreateAsync(
            new MinimalMessageRequest
            {
                Model = configuration.ModelId,
                MaxTokens = 32,
                Messages =
                [
                    new MinimalMessage
                    {
                        Role = RuntimeProviderRoles.User,
                        Text = "Reply with exactly OK and no other text."
                    }
                ]
            });

        var text = string.Concat(
            response.Content
                .Where(static block => string.Equals(block.Type, "text", StringComparison.Ordinal))
                .Select(static block => block.Text));

        Assert.AreEqual("OK", text.Trim());
        Assert.AreEqual("end_turn", response.StopReason);
        Assert.IsNotNull(response.Usage);
        Assert.IsGreaterThan(0, response.Usage!.InputTokens);
        Assert.IsGreaterThan(0, response.Usage.OutputTokens);
    }

    [TestMethod]
    [TestCategory("Anthropic")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnthropicMinimal_ThinkingStreaming_ReturnsReasoningAndText()
    {
        var configuration = RequireAnthropicConfiguration();
        using var client = new AnthropicMinimalClient(
            new AnthropicMinimalClientOptions
            {
                ApiKey = configuration.ApiKey,
                BaseUrl = configuration.BaseUrl,
                Timeout = TimeSpan.FromSeconds(90)
            });

        var events = new List<MinimalStreamEvent>();
        await foreach (var streamEvent in client.Messages.CreateStreamingAsync(
                           new MinimalMessageRequest
                           {
                               Model = configuration.ModelId,
                               MaxTokens = 2048,
                               Thinking = new MinimalThinkingOptions
                               {
                                   Type = "enabled",
                                   BudgetTokens = 1024
                               },
                               Messages =
                               [
                                   new MinimalMessage
                                   {
                                       Role = RuntimeProviderRoles.User,
                                       Text = "What is 17 multiplied by 19? Briefly explain the calculation."
                                   }
                               ]
                           }))
        {
            events.Add(streamEvent);
        }

        Assert.IsTrue(events.Any(static item => !string.IsNullOrWhiteSpace(item.Thinking)));
        Assert.IsTrue(events.Any(static item => !string.IsNullOrWhiteSpace(item.Text)));
        Assert.IsTrue(events.Any(static item => item.Type == "message_stop"));
    }

    [TestMethod]
    [TestCategory("Anthropic")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnthropicAdapter_RealToolCall_EmitsIncrementalArgumentsAndCompletion()
    {
        var configuration = RequireAnthropicConfiguration();
        using var adapter = new AnthropicProviderAdapter(
            configuration.ApiKey,
            configuration.BaseUrl,
            [
                new ProviderModel(
                    configuration.ModelId,
                    configuration.ModelId,
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
            ]);
        ToolDescriptor[] tools =
        [
            new(
                "weather",
                "host",
                "get_weather",
                "Returns the current weather for a city.",
                """
                {
                  "type": "object",
                  "properties": {
                    "city": { "type": "string" }
                  },
                  "required": ["city"],
                  "additionalProperties": false
                }
                """)
        ];

        var request = new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "real-anthropic-tool-agent",
            adapter.ProviderId,
            configuration.ModelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [
                        new TextContentBlock(
                            "You must call the get_weather tool for Shanghai now. Do not answer with text.")
                    ])
            ],
            tools,
            MaxTokens: 128);

        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, request);
        var deltas = events.OfType<ToolCallDeltaProviderEvent>().ToArray();
        var completed = Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());

        Assert.IsNotEmpty(deltas);
        Assert.IsFalse(string.IsNullOrWhiteSpace(deltas[0].CallId));
        Assert.AreEqual("get_weather", deltas[0].Name);
        using var deltaArguments = JsonDocument.Parse(
            string.Concat(deltas.Select(static item => item.ArgumentsDelta)));
        Assert.AreEqual(
            "Shanghai",
            deltaArguments.RootElement.GetProperty("city").GetString());
        Assert.AreEqual(deltas[0].CallId, completed.CallId);
        Assert.AreEqual("weather", completed.ToolId);
        Assert.AreEqual("get_weather", completed.Name);
        using var completeArguments = JsonDocument.Parse(completed.ArgumentsJson);
        Assert.AreEqual(
            "Shanghai",
            completeArguments.RootElement.GetProperty("city").GetString());
        Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());

        using var toolArguments = JsonDocument.Parse(completed.ArgumentsJson);
        var continuationRequest = request with
        {
            InvocationId = Guid.NewGuid().ToString("N"),
            Messages =
            [
                request.Messages[0],
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.Assistant,
                    [
                        new ToolCallContentBlock(
                            completed.CallId,
                            completed.ToolId,
                            completed.Name,
                            toolArguments.RootElement.Clone())
                    ]),
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [
                        new ToolResultContentBlock(
                            completed.CallId,
                            completed.ToolId,
                            true,
                            [new TextContentBlock("Weather in Shanghai is sunny and 25 degrees Celsius.")])
                    ])
            ]
        };

        var continuationEvents = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            continuationRequest);
        var continuationText = string.Concat(
            continuationEvents
                .OfType<TextDeltaProviderEvent>()
                .Select(static item => item.Delta));

        Assert.IsFalse(string.IsNullOrWhiteSpace(continuationText));
        Assert.ContainsSingle(continuationEvents.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(continuationEvents.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(continuationEvents.OfType<InvocationFailedProviderEvent>());
    }

    [TestMethod]
    [TestCategory("Anthropic")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnthropicAdapter_RealCancellation_StopsStreamWithoutWaitingForText()
    {
        var configuration = RequireAnthropicConfiguration();
        using var adapter = new AnthropicProviderAdapter(
            configuration.ApiKey,
            configuration.BaseUrl,
            [
                new ProviderModel(
                    configuration.ModelId,
                    configuration.ModelId,
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
            ]);
        using var cancellation = new CancellationTokenSource();
        var request = new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "real-anthropic-cancel-agent",
            adapter.ProviderId,
            configuration.ModelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [
                        new TextContentBlock(
                            "Write a detailed 1500-word technical essay about distributed systems. Continue until cancelled.")
                    ])
            ],
            MaxTokens: 1024,
            CancellationToken: cancellation.Token);

        var streamTask = ConsumeStreamAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        cancellation.Cancel();

        var completed = await Task.WhenAny(
            streamTask,
            Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.AreSame(streamTask, completed, "The stream did not stop promptly after cancellation.");
        await Assert.ThrowsAsync<OperationCanceledException>(() => streamTask);
        Assert.IsTrue(cancellation.IsCancellationRequested);

        async Task ConsumeStreamAsync()
        {
            await foreach (var _ in adapter.CompleteStreamingAsync(request, cancellation.Token)
                               .WithCancellation(cancellation.Token))
            {
            }
        }
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ConfiguredProvider_ReturnsTerminalEvent(ProviderAdapterKind kind)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_PROVIDER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_PROVIDER_SMOKE=1 to run opt-in real Provider smoke tests.");
        }

        var prefix = kind switch
        {
            ProviderAdapterKind.OpenAI => "MADORIN_OPENAI",
            ProviderAdapterKind.Anthropic => "MADORIN_ANTHROPIC",
            ProviderAdapterKind.OpenAICompatible => "MADORIN_COMPATIBLE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var apiKey = Environment.GetEnvironmentVariable($"{prefix}_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable($"{prefix}_BASE_URL");
        var modelId = Environment.GetEnvironmentVariable($"{prefix}_MODEL_ID");
        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(modelId))
        {
            Assert.Inconclusive($"{prefix}_API_KEY, _BASE_URL, and _MODEL_ID are required.");
        }

        ProviderModel[] models =
        [
            new(
                modelId,
                modelId,
                Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
        ];
        IRuntimeProviderAdapter adapter = kind switch
        {
            ProviderAdapterKind.OpenAI => new OpenAIProviderAdapter(apiKey, baseUrl, models),
            ProviderAdapterKind.Anthropic => new AnthropicProviderAdapter(apiKey, baseUrl, models),
            ProviderAdapterKind.OpenAICompatible => new OpenAICompatibleProviderAdapter(
                new OpenAICompatibleProviderOptions(
                    "compatible-smoke",
                    baseUrl,
                    apiKey,
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities(),
                    Models: models),
                ProviderHttpClientFactory.Shared.CreateClient("compatible-smoke"),
                disposeHttpClient: true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        using var adapterLifetime = adapter as IDisposable;
        var request = new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "smoke-agent",
            adapter.ProviderId,
            modelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("Reply with OK.")])
            ],
            MaxTokens: 16);

        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, request);

        Assert.IsTrue(events.Any(static item => item is TextDeltaProviderEvent));
        Assert.IsTrue(events.Any(static item => item is InvocationCompletedProviderEvent));
        Assert.IsFalse(events.Any(static item => item is InvocationFailedProviderEvent));
    }

    private static AnthropicSmokeConfiguration RequireAnthropicConfiguration()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_PROVIDER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_PROVIDER_SMOKE=1 to run opt-in real Provider smoke tests.");
        }

        var apiKey = Environment.GetEnvironmentVariable("MADORIN_ANTHROPIC_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("MADORIN_ANTHROPIC_BASE_URL");
        var modelId = Environment.GetEnvironmentVariable("MADORIN_ANTHROPIC_MODEL_ID");
        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(modelId))
        {
            Assert.Inconclusive(
                "MADORIN_ANTHROPIC_API_KEY, MADORIN_ANTHROPIC_BASE_URL, and MADORIN_ANTHROPIC_MODEL_ID are required.");
        }

        return new AnthropicSmokeConfiguration(apiKey, baseUrl, modelId);
    }

    private sealed record AnthropicSmokeConfiguration(
        string ApiKey,
        string BaseUrl,
        string ModelId);
}
