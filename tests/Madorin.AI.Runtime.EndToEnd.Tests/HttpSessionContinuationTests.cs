using System.Net;
using System.Net.Http.Json;
using Madorin.AI.Runtime.Contracts.Http;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public class HttpSessionContinuationTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task SessionContinuation_PreservesContext()
    {
        await using var runtime = await HttpRuntimeFixture.StartAsync();
        using var httpClient = new HttpClient { BaseAddress = runtime.Address, Timeout = TimeSpan.FromSeconds(15) };
        {

            // Act 1: First call without sessionId
            var firstRequest = new HttpRunRequest
            {
                Input = "Remember this number: 42",
                ProviderId = "test-provider",
                ModelId = "test-model"
            };

            var firstResponse = await httpClient.PostAsJsonAsync(
                "/v1/run",
                firstRequest);

            Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode,
                await firstResponse.Content.ReadAsStringAsync());

            var firstResult = await firstResponse.Content.ReadFromJsonAsync<HttpRunResponse>();
            Assert.IsNotNull(firstResult);
            Assert.IsNotNull(firstResult.SessionId);
            Assert.IsFalse(string.IsNullOrEmpty(firstResult.SessionId));

            var capturedSessionId = firstResult.SessionId;

            // Act 2: Second call WITH the sessionId
            var secondRequest = new HttpRunRequest
            {
                SessionId = capturedSessionId,
                Input = "What number did I tell you to remember?",
                ProviderId = "test-provider",
                ModelId = "test-model"
            };

            var secondResponse = await httpClient.PostAsJsonAsync(
                "/v1/run",
                secondRequest);

            Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode,
                await secondResponse.Content.ReadAsStringAsync());

            var secondResult = await secondResponse.Content.ReadFromJsonAsync<HttpRunResponse>();
            Assert.IsNotNull(secondResult);

            Assert.AreEqual(capturedSessionId, secondResult.SessionId);
            Assert.AreEqual("The number was 42.", secondResult.Output);
            Assert.HasCount(2, runtime.Provider.Requests);
            var secondMessages = runtime.Provider.Requests[1].Messages;
            Assert.IsTrue(secondMessages.Any(message => message.Content
                .OfType<Madorin.AI.Runtime.Contracts.TextContentBlock>()
                .Any(block => block.Text.Contains("Remember this number: 42", StringComparison.Ordinal))));
        }
    }
}
