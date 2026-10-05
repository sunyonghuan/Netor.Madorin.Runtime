using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.Tests.HostClient;

[TestClass]
public sealed class RunRequestFactoryTests
{
    private static readonly RunAgentDraft Primary = new(
        "primary",
        "provider",
        "model");

    [TestMethod]
    public void CreateSelection_ExpertDraft_CreatesExpertOptions()
    {
        var draft = new RunRequestDraft(RuntimeMode.Expert, "hello", Primary);

        var selection = RunRequestFactory.CreateSelection(draft);

        Assert.AreEqual(RuntimeMode.Expert, selection.Mode);
        Assert.IsInstanceOfType<ExpertModeOptions>(selection.ModeOptions);
        Assert.AreEqual("provider", selection.DefaultSelection.ProviderId);
    }

    [TestMethod]
    public void CreateSelection_MissingProvider_ThrowsArgumentException()
    {
        var draft = new RunRequestDraft(
            RuntimeMode.Expert,
            "hello",
            Primary with { ProviderId = string.Empty });

        Assert.ThrowsExactly<ArgumentException>(() => RunRequestFactory.CreateSelection(draft));
    }
}
