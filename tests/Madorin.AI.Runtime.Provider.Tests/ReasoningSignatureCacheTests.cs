using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ReasoningSignatureCacheTests
{
    [TestMethod]
    public void Draft_AppendsAnthropicFragmentsAndReplacesOpenAISignature()
    {
        var append = new ReasoningSignatureDraft(appendFragments: true);
        Assert.AreEqual("thi", append.Add("thi", null));
        Assert.AreEqual("nk", append.Add("nk", null));
        Assert.IsNull(append.Add(null, "aa"));
        Assert.IsNull(append.Add(null, "bb"));

        var appendCache = new ReasoningSignatureCache();
        append.Commit(appendCache, "anthropic-a");
        Assert.AreEqual("aabb", appendCache.TryGet("anthropic-a", "think"));
        Assert.IsNull(appendCache.TryGet("anthropic-b", "think"));

        var replace = new ReasoningSignatureDraft(appendFragments: false);
        replace.Add("think", "aa");
        replace.Add(null, "bb");
        var replaceCache = new ReasoningSignatureCache();
        replace.Commit(replaceCache, "openai-a");
        Assert.AreEqual("bb", replaceCache.TryGet("openai-a", "think"));
        Assert.IsNull(replaceCache.TryGet("openai-a", "other"));
    }

    [TestMethod]
    public void Draft_StartsANewBlockAfterTextOrToolBoundary()
    {
        var draft = new ReasoningSignatureDraft(appendFragments: true);
        draft.Add("one", "sig-1");
        draft.EndBlock();
        draft.Add("two", "sig-2");

        var cache = new ReasoningSignatureCache();
        draft.Commit(cache, "provider-a");
        Assert.AreEqual("sig-1", cache.TryGet("provider-a", "one"));
        Assert.AreEqual("sig-2", cache.TryGet("provider-a", "two"));
    }

    [TestMethod]
    public void Sweep_RemovesExpiredSignaturesAndKeepsTouchedConversations()
    {
        var time = new ManualTimeProvider();
        var retention = TimeSpan.FromMinutes(30);
        var active = new ReasoningSignatureCache(time, retention);
        var idle = new ReasoningSignatureCache(time, retention);
        active.Store("provider-a", "keep", "active-signature");
        idle.Store("provider-a", "drop", "idle-signature");
        idle.Store("provider-b", "drop", "other-provider");

        time.Now = time.Now.AddMinutes(31);
        active.Touch(
            "provider-a",
            [new RuntimeProviderMessage(RuntimeProviderRoles.Assistant, [new ReasoningContentBlock("keep")])]);
        active.Sweep();
        idle.Sweep();

        Assert.AreEqual("active-signature", active.TryGet("provider-a", "keep"));
        Assert.IsNull(idle.TryGet("provider-a", "drop"));
        Assert.IsNull(idle.TryGet("provider-b", "drop"));
        Assert.IsNull(active.TryGet("provider-b", "keep"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
