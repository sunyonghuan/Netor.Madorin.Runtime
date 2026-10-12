using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Modes.Expert;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ExpertExecutionEvidenceTests
{
    private static readonly ExpertToolEvidence Good = new("temp-one", "call-good", "mcli_fs_read", true, DateTimeOffset.UtcNow);
    private static readonly ExpertToolEvidence Bad = new("temp-one", "call-bad", "mcli_fs_read", false, DateTimeOffset.UtcNow);

    [TestMethod]
    public void Tools_NoEvidence_DoesNotComplete() =>
        Assert.IsNotNull(ExpertExecutionEvidence.Validate("tools", null, "I succeeded", []));

    [TestMethod]
    public void Tools_FailedToolFollowedBySuccess_DoesNotComplete() =>
        Assert.IsNotNull(ExpertExecutionEvidence.Validate("tools", null, "Everything succeeded", [Bad, Good]));

    [TestMethod]
    public void Tools_SuccessfulInvocation_Completes() =>
        Assert.IsNull(ExpertExecutionEvidence.Validate("tools", null, "done", [Good]));

    [TestMethod]
    public void Analysis_RequiresExactDeclaredCriterion()
    {
        Assert.IsNotNull(ExpertExecutionEvidence.Validate("analysis", null, "ok", []));
        Assert.IsNotNull(ExpertExecutionEvidence.Validate("analysis", "expected", "wrong", []));
        Assert.IsNull(ExpertExecutionEvidence.Validate("analysis", "expected", "expected", []));
        Assert.IsNotNull(ExpertExecutionEvidence.Validate("analysis", "expected", "expected", [Bad]));
    }
}
