using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class HostProviderConfigContractTests
{
    [TestMethod]
    public void HostProviderConfig_RoundTripsThroughRuntimeJsonContext()
    {
        var expected = new HostProviderConfig(
            "provider-1",
            "OpenAI",
            "https://api.openai.com/v1",
            "sk-test",
            AuthenticationHeader: "Authorization",
            AuthenticationScheme: "Bearer",
            Models: ["gpt-5"],
            ConfigurationVersion: "1");

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.HostProviderConfig);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.HostProviderConfig);

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.ProviderId, actual.ProviderId);
        Assert.AreEqual(expected.Protocol, actual.Protocol);
        Assert.AreEqual(expected.BaseUrl, actual.BaseUrl);
        Assert.AreEqual(expected.ApiKey, actual.ApiKey);
        CollectionAssert.AreEqual(expected.Models!.ToArray(), actual.Models!.ToArray());
        StringAssert.Contains(json, "\"providerId\":\"provider-1\"");
        StringAssert.Contains(json, "\"apiKey\":\"sk-test\"");
    }

    [TestMethod]
    public void NewSessionRunRequest_SerializesProviderConfigs()
    {
        var expected = new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("provider-1", "model-1"),
                new ExpertModeOptions(new AgentRef("agent-1", "v1", "System"))),
            [new TextContentBlock("hi")],
            ProviderConfigs:
            [
                new HostProviderConfig(
                    "provider-1",
                    "OpenAI",
                    "https://api.openai.com/v1",
                    "sk-test")
            ]);

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.NewSessionRunRequest);

        Assert.IsNotNull(actual);
        Assert.IsNotNull(actual.ProviderConfigs);
        Assert.HasCount(1, actual.ProviderConfigs);
        Assert.AreEqual("provider-1", actual.ProviderConfigs[0].ProviderId);
        StringAssert.Contains(json, "\"providerConfigs\"");
    }

    [TestMethod]
    public void ExistingSessionRunRequest_And_SelectionUpdate_RoundTripProviderConfigs()
    {
        var existing = new ExistingSessionRunRequest(
            "session-1",
            "run-2",
            InputOverride: [new TextContentBlock("next")],
            ProviderConfigs:
            [
                new HostProviderConfig(
                    "provider-2",
                    "Anthropic",
                    "https://api.anthropic.com",
                    "sk-2")
            ]);
        var existingJson = JsonSerializer.Serialize(
            existing,
            RuntimeJsonContext.Default.ExistingSessionRunRequest);
        var existingActual = JsonSerializer.Deserialize(
            existingJson,
            RuntimeJsonContext.Default.ExistingSessionRunRequest);
        Assert.IsNotNull(existingActual?.ProviderConfigs);
        Assert.AreEqual("provider-2", existingActual.ProviderConfigs[0].ProviderId);

        var update = new SessionSelectionUpdateParameters(
            "session-1",
            ExpectedSelectionVersion: 1,
            Selection: new NextTurnSelection(
                2,
                RuntimeMode.Expert,
                new DefaultSelection("provider-2", "model-2"),
                new ExpertModeOptions(new AgentRef("a", "1", "p"))),
            ProviderConfigs:
            [
                new HostProviderConfig(
                    "provider-2",
                    "Anthropic",
                    "https://api.anthropic.com",
                    "sk-2")
            ]);
        var updateJson = JsonSerializer.Serialize(
            update,
            RuntimeJsonContext.Default.SessionSelectionUpdateParameters);
        var updateActual = JsonSerializer.Deserialize(
            updateJson,
            RuntimeJsonContext.Default.SessionSelectionUpdateParameters);
        Assert.IsNotNull(updateActual?.ProviderConfigs);
        Assert.AreEqual("provider-2", updateActual.ProviderConfigs[0].ProviderId);
    }

    [TestMethod]
    public void ProvidersUpsertParameters_RoundTrips()
    {
        var expected = new ProvidersUpsertParameters(
        [
            new HostProviderConfig(
                "p1",
                "OpenAI Compatible",
                "https://compat.example/v1",
                "key")
        ]);
        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.ProvidersUpsertParameters);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.ProvidersUpsertParameters);
        Assert.IsNotNull(actual);
        Assert.HasCount(1, actual.Providers);
        Assert.AreEqual("p1", actual.Providers[0].ProviderId);
        Assert.AreEqual("OpenAI Compatible", actual.Providers[0].Protocol);
    }
}
