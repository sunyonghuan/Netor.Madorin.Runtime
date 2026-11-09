using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.MConsole.LiveHost;

namespace Madorin.AI.Runtime.MConsole.Tests.LiveHost;

[TestClass]
public sealed class DebugConsoleTests
{
    [TestMethod]
    public void Parse_RecognizesModeProviderAndPlainPrompt()
    {
        Assert.AreEqual(DebugCommandKind.Mode, DebugCommandParser.Parse("/mode work").Kind);
        Assert.AreEqual("work", DebugCommandParser.Parse("/MODE work").Argument);
        Assert.AreEqual(DebugCommandKind.Provider, DebugCommandParser.Parse("/provider deepseek").Kind);
        Assert.AreEqual("你好", DebugCommandParser.Parse("  你好  ").Argument);
        Assert.AreEqual(DebugCommandKind.Unknown, DebugCommandParser.Parse("/nope").Kind);
        Assert.AreEqual(DebugCommandKind.Quit, DebugCommandParser.Parse("/exit").Kind);
    }

    [TestMethod]
    public void Format_TextDelta_ProjectsAssistantTextAndEventSummary()
    {
        using var document = JsonDocument.Parse("{\"invocationId\":\"i\",\"delta\":\"你好\"}");
        var view = DebugEventFormatter.Format(CreateEnvelope(MessageTypes.TextDelta, document.RootElement));

        Assert.AreEqual(DebugOutputChannel.Assistant, view.Channel);
        Assert.AreEqual("你好", view.Text);
        Assert.IsFalse(view.Terminal);
        Assert.IsTrue(view.EventLine.Contains("output.delta", StringComparison.Ordinal));
        Assert.IsTrue(view.EventLine.Contains("gsn=3", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Format_RunFailed_BecomesNotice()
    {
        using var document = JsonDocument.Parse(
            "{\"runId\":\"r\",\"sessionId\":\"s\",\"error\":{\"code\":\"Boom\",\"message\":\"模型拒绝\"}}");
        var view = DebugEventFormatter.Format(CreateEnvelope(MessageTypes.RunFailed, document.RootElement));

        Assert.AreEqual(DebugOutputChannel.Notice, view.Channel);
        Assert.AreEqual("Run failed: 模型拒绝", view.Text);
        Assert.IsTrue(view.Terminal);
    }

    [TestMethod]
    public void Format_PlanRestored_BecomesNoticeWithGoalAndStepCount()
    {
        using var document = JsonDocument.Parse(
            "{\"planId\":\"p\",\"goal\":\"排查空基路径\",\"stepCount\":3}");
        var view = DebugEventFormatter.Format(CreateEnvelope(MessageTypes.ExpertPlanRestored, document.RootElement));

        Assert.AreEqual(DebugOutputChannel.Notice, view.Channel);
        Assert.AreEqual("[已恢复未完成计划]（3 步） 排查空基路径", view.Text);
        Assert.IsFalse(view.Terminal);
    }

    [TestMethod]
    public void Transcript_GroupsReasoningBeforeAssistantText()
    {
        var transcript = new DebugTranscript();
        transcript.Append(DebugOutputChannel.Reasoning, "甲");
        transcript.Append(DebugOutputChannel.Reasoning, "乙");
        transcript.Append(DebugOutputChannel.Assistant, "答");

        Assert.AreEqual("\n[思考]\n甲乙\n答", transcript.OutputText);
    }

    [TestMethod]
    public void Transcript_AggregatesConsecutiveReasoningAndOutputEvents()
    {
        var transcript = new DebugTranscript();
        var timestamp = new DateTimeOffset(2026, 10, 5, 1, 2, 3, TimeSpan.Zero);
        transcript.AddEvent(MessageTypes.ReasoningDelta, 1, timestamp, "delta", 2, aggregate: true);
        transcript.AddEvent(MessageTypes.ReasoningDelta, 4, timestamp, "delta", 3, aggregate: true);
        transcript.AddEvent(MessageTypes.TextDelta, 5, timestamp, "delta", 4, aggregate: true);
        transcript.AddEvent(MessageTypes.TextDelta, 8, timestamp, "delta", 1, aggregate: true);
        transcript.AddEvent(MessageTypes.RunCompleted, 9, timestamp, "done", 0, aggregate: false);

        var events = transcript.Events;
        Assert.HasCount(3, events);
        Assert.IsTrue(events[0].Contains("reasoning.delta ×2 +5", StringComparison.Ordinal));
        Assert.IsTrue(events[0].Contains("gsn=1-4", StringComparison.Ordinal));
        Assert.IsTrue(events[1].Contains("output.delta ×2 +5", StringComparison.Ordinal));
        Assert.IsTrue(events[1].Contains("gsn=5-8", StringComparison.Ordinal));
        Assert.AreEqual("done", events[2]);
    }

    [TestMethod]
    public void Load_KeepsApiKeyOutOfDescription()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mconsole-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "config.json"),
                """
                {
                  "defaultProvider": "deepseek",
                  "defaultModel": "deepseek-chat",
                  "defaultAgent": "default",
                  "providers": [
                    {
                      "name": "deepseek",
                      "protocol": "DeepSeek",
                      "baseUrl": "https://api.example",
                      "apiKey": "SECRET-KEY-VALUE",
                      "models": ["deepseek-chat"]
                    }
                  ]
                }
                """);

            var snapshot = DebugConfigSnapshot.Load(directory);

            Assert.HasCount(1, snapshot.HostConfigs);
            Assert.AreEqual("deepseek", snapshot.HostConfigs[0].Protocol);
            Assert.AreEqual("SECRET-KEY-VALUE", snapshot.HostConfigs[0].ApiKey);
            Assert.IsFalse(snapshot.Describe().Contains("SECRET-KEY-VALUE", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Create_ExpertModeWithoutAgents_UsesDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mconsole-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.json"), """{"defaultProvider":"p","defaultModel":"m"}""");
            var snapshot = DebugConfigSnapshot.Load(directory);
            var selection = DebugSelectionFactory.Create(RuntimeMode.Expert, snapshot, "p", "m", 1);

            var expert = selection.ModeOptions as ExpertModeOptions;
            Assert.IsNotNull(expert);
            Assert.AreEqual("default", expert.Agent.AgentId);
            Assert.AreEqual("p", selection.DefaultSelection.ProviderId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RuntimeEventEnvelope CreateEnvelope(string messageType, JsonElement payload) =>
        new("runtime", 3, "run", 1, messageType, new DateTimeOffset(2026, 10, 5, 1, 2, 3, TimeSpan.Zero), payload);
}
