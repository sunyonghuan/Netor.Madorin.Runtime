using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Contracts.Http;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public class HttpSessionContinuationTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task SessionContinuation_PreservesContext()
    {
        // Arrange
        var port = GetAvailablePort();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = new CancellationTokenSource();

        try
        {
            var options = new RuntimeServerOptions(tempDir)
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                PipePrefix = $"test-pipe-{Guid.NewGuid():N}",
                HttpPort = port,
                HandshakeSecret = secret
            };

            await using var server = await RuntimeServer.StartAsync(options);
            var serverTask = server.RunAsync(shutdown.Token);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            // Act 1: First call without sessionId
            var firstRequest = new HttpRunRequest
            {
                Input = "Remember this number: 42",
                ProviderId = "mock",
                ModelId = "mock-model"
            };

            var firstResponse = await httpClient.PostAsJsonAsync(
                $"http://localhost:{port}/v1/run",
                firstRequest);

            Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);

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
                ProviderId = "mock",
                ModelId = "mock-model"
            };

            var secondResponse = await httpClient.PostAsJsonAsync(
                $"http://localhost:{port}/v1/run",
                secondRequest);

            Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);

            var secondResult = await secondResponse.Content.ReadFromJsonAsync<HttpRunResponse>();
            Assert.IsNotNull(secondResult);

            // Assert: Session ID should be preserved
            Assert.AreEqual(capturedSessionId, secondResult.SessionId);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
