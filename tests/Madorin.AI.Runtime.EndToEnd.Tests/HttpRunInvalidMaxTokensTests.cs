using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Http;
using Madorin.AI.Runtime.Server.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

/// <summary>
/// Tests for /v1/run endpoint with invalid maxTokens parameter to verify validation error handling.
/// </summary>
[TestClass]
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access",
    Justification = "Test code does not require AOT compatibility")]
[UnconditionalSuppressMessage(
    "AOT",
    "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality",
    Justification = "Test code does not require AOT compatibility")]
public class HttpRunInvalidMaxTokensTests
{
    /// <summary>
    /// Test that /v1/run endpoint returns HTTP 400 validation error when maxTokens=-1.
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task RunEndpoint_WithNegativeMaxTokens_ReturnsValidationError()
    {
        // Arrange: Set up HTTP server
        var port = GetAvailablePort();
        var pipeName = $"test-pipe-{Guid.NewGuid():N}";
        var instanceId = Guid.NewGuid().ToString("N");
        var handshakeSecret = Guid.NewGuid().ToString("N");

        await using var httpServer = new RuntimeHttpServer(
            "localhost",
            port,
            pipeName,
            instanceId,
            handshakeSecret,
            NullLoggerFactory.Instance);

        await httpServer.StartAsync();

        // Create HTTP client to call /v1/run
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{port}")
        };

        var request = new HttpRunRequest
        {
            ModelId = "test-model",
            Input = "Hello, world!",
            ProviderId = "test-provider",
            RequestControl = new ExpertRequestControl
            {
                MaxTokens = -1  // Invalid: must be >= 1
            }
        };

        // Act: Call /v1/run endpoint
        var response = await client.PostAsJsonAsync("/v1/run", request);

        // Assert: Verify validation error response
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonDocument.Parse(content);

        Assert.IsTrue(problemDetails.RootElement.TryGetProperty("title", out var title));
        Assert.AreEqual("Invalid request parameters", title.GetString());

        Assert.IsTrue(problemDetails.RootElement.TryGetProperty("detail", out var detail));
        var detailText = detail.GetString();
        Assert.IsNotNull(detailText);
        Assert.IsTrue(
            detailText.Contains("MaxTokens") && detailText.Contains(">= 1"),
            $"Expected validation error message about MaxTokens >= 1, got: {detailText}");

        Assert.IsTrue(problemDetails.RootElement.TryGetProperty("status", out var status));
        Assert.AreEqual(400, status.GetInt32());
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
