using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Services.Prompt;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class ConversationEnvironmentPromptTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Append_UsesRequestedLanguageAndLocalDate()
    {
        var now = new DateTimeOffset(2026, 9, 22, 8, 30, 0, TimeSpan.FromHours(8));
        var text = ConversationEnvironmentPrompt.Append(
            "persona",
            @"E:\work",
            " en-US ",
            now);

        StringAssert.StartsWith(text, "persona\n\n## Environment\n");
        StringAssert.Contains(text, "locale: en-US");
        StringAssert.Contains(text, "date: 2026-09-22 +08:00");
        StringAssert.Contains(text, @"workspace: E:\work");
        StringAssert.Contains(text, "cli: madorin ");
    }

    [TestMethod]
    public void Append_NullOrBlankLanguage_UsesOperatingSystemUiCulture()
    {
        var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.FromHours(-5));
        var expected = "locale: " + CultureInfo.CurrentUICulture.Name;

        var omitted = ConversationEnvironmentPrompt.Append("persona", @"E:\work", null, now);
        var blank = ConversationEnvironmentPrompt.Append("persona", @"E:\work", "   ", now);

        StringAssert.Contains(omitted, expected);
        StringAssert.Contains(blank, expected);
        StringAssert.Contains(omitted, "date: 2026-01-02 -05:00");
    }

    [TestMethod]
    public void Language_RoundTripsAndNullIsOmitted()
    {
        var request = CreateRequest("zh-CN");
        var json = JsonSerializer.Serialize(request, RuntimeJsonContext.Default.NewSessionRunRequest);
        StringAssert.Contains(json, "\"language\":\"zh-CN\"");

        var omitted = JsonSerializer.Serialize(
            request with { Language = null },
            RuntimeJsonContext.Default.NewSessionRunRequest);
        Assert.IsFalse(omitted.Contains("language", StringComparison.Ordinal));

        var existing = new ExistingSessionRunRequest("session-1", "run-1", Language: "en-US");
        var existingJson = JsonSerializer.Serialize(
            existing,
            RuntimeJsonContext.Default.ExistingSessionRunRequest);
        StringAssert.Contains(existingJson, "\"language\":\"en-US\"");
    }

    private static NewSessionRunRequest CreateRequest(string? language) =>
        new(
            "session-key",
            "run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("provider", "model"),
                new ExpertModeOptions(new AgentRef("agent", "1", "prompt"))),
            [],
            Language: language);
}
