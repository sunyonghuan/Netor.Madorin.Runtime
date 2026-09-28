using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Meeting;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class MeetingReasoningAccumulationTests
{
    [TestMethod]
    public void AccumulateContent_MergesSplitReasoningDeltasIntoTheReplayText()
    {
        var content = new List<ContentBlock>();
        MeetingInvocationExecutor.AccumulateContent(
            content,
            new ReasoningDeltaProviderEvent("inv-1", "thi"));
        MeetingInvocationExecutor.AccumulateContent(
            content,
            new ReasoningDeltaProviderEvent("inv-1", "nk"));

        var reasoning = Assert.ContainsSingle(content.OfType<ReasoningContentBlock>());
        Assert.AreEqual("think", reasoning.Content);
        Assert.HasCount(1, content);

        var draft = new ReasoningSignatureDraft(appendFragments: true);
        draft.Add("thi", null);
        draft.Add("nk", null);
        draft.Add(null, "sig-");
        draft.Add(null, "nature");
        var signatures = new ReasoningSignatureCache();
        draft.Commit(signatures, "meeting-provider");
        Assert.AreEqual("sig-nature", signatures.TryGet("meeting-provider", reasoning.Content));
    }

    [TestMethod]
    public void AccumulateContent_StartsANewReasoningBlockAfterText()
    {
        var content = new List<ContentBlock>();
        MeetingInvocationExecutor.AccumulateContent(
            content,
            new ReasoningDeltaProviderEvent("inv-1", "first"));
        MeetingInvocationExecutor.AccumulateContent(
            content,
            new TextDeltaProviderEvent("inv-1", "answer"));
        MeetingInvocationExecutor.AccumulateContent(
            content,
            new ReasoningDeltaProviderEvent("inv-1", "second"));

        var reasoning = content.OfType<ReasoningContentBlock>().ToArray();
        Assert.HasCount(2, reasoning);
        Assert.AreEqual("first", reasoning[0].Content);
        Assert.AreEqual("second", reasoning[1].Content);
        Assert.AreEqual("answer", Assert.ContainsSingle(content.OfType<TextContentBlock>()).Text);
    }
}
