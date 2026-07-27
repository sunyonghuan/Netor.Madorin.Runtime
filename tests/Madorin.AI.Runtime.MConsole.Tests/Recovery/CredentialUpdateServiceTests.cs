using Madorin.AI.Runtime.MConsole.Recovery;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Recovery;

[TestClass]
[DoNotParallelize]
public sealed class CredentialUpdateServiceTests
{
    private const string CredentialVariable = "MADORIN_MCONSOLE_TEST_CREDENTIAL";

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task UpdateViaSdkAsync_ReadsEnvironmentWithoutPuttingValueInAudit()
    {
        const string credential = "test-secret-value";
        var previous = Environment.GetEnvironmentVariable(CredentialVariable);
        Environment.SetEnvironmentVariable(CredentialVariable, credential);
        try
        {
            var client = new FakeRuntimeClient();
            var service = new CredentialUpdateService(client);
            var draft = new CredentialUpdateDraft(
                "run",
                "provider",
                CredentialVariable,
                "production",
                DateTimeOffset.UtcNow.AddHours(1));

            var audit = await service.UpdateViaSdkAsync(
                draft,
                TestContext.CancellationToken);

            Assert.AreEqual(credential, client.LastCredentialsUpdate!.Credential);
            Assert.AreEqual(CredentialVariable, audit.CredentialEnvironmentVariable);
            Assert.DoesNotContain(credential, audit.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialVariable, previous);
        }
    }

    [TestMethod]
    public async Task UpdateViaSdkAsync_MissingEnvironmentVariable_FailsBeforeClientCall()
    {
        var previous = Environment.GetEnvironmentVariable(CredentialVariable);
        Environment.SetEnvironmentVariable(CredentialVariable, null);
        try
        {
            var client = new FakeRuntimeClient();
            var service = new CredentialUpdateService(client);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.UpdateViaSdkAsync(
                    new CredentialUpdateDraft("run", "provider", CredentialVariable),
                    TestContext.CancellationToken));

            Assert.IsNull(client.LastCredentialsUpdate);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialVariable, previous);
        }
    }
}
