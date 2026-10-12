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
/// Tests for /v1/run endpoint with stopSequences parameter to verify early termination behavior.
/// </summary>
[TestClass]
public class HttpRunStopSequencesTests
{
    private static readonly JsonSerializerOptions HttpJsonOptions = new(JsonSerializerDefaults.Web);
    /// <summary>
    /// Test that /v1/run endpoint respects stopSequences parameter and terminates early when encountering STOP or END.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RunEndpoint_WithStopSequences_TerminatesEarly()
    {
        await using var runtime = await HttpRuntimeFixture.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = runtime.Address,
            Timeout = TimeSpan.FromSeconds(15)
        };

        var request = new HttpRunRequest
        {
            ModelId = "test-model",
            Input = "Count from 1 to 100 and say STOP when you reach 50, then continue to END",
            ProviderId = "test-provider",
            RequestControl = new ExpertRequestControl
            {
                StopSequences = ["STOP", "END"],
                MaxTokens = 1000,
                Temperature = 0.7
            }
        };

        // Act: Call /v1/run endpoint
        var response = await client.PostAsJsonAsync("/v1/run", request);

        // Assert: Verify response structure
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var runResponse = JsonSerializer.Deserialize<HttpRunResponse>(content, HttpJsonOptions);

        Assert.IsNotNull(runResponse);
        Assert.IsNotNull(runResponse.RunId);
        Assert.IsNotNull(runResponse.SessionId);
        Assert.IsNotNull(runResponse.Output);

        Assert.AreEqual("1 2 3 ", runResponse.Output, $"{content}; calls={runtime.Provider.Requests.Count}");
        Assert.IsFalse(runResponse.Output.Contains("STOP", StringComparison.Ordinal));
        Assert.IsFalse(runResponse.Output.Contains("100", StringComparison.Ordinal));
        var providerRequest = Assert.ContainsSingle(runtime.Provider.Requests);
        CollectionAssert.AreEqual(request.RequestControl.StopSequences, providerRequest.RequestControl?.StopSequences);
        Assert.IsFalse(providerRequest.Stream);

        // Verify status is Completed
        Assert.AreEqual("Completed", runResponse.Status);

        // Verify token counts are available
        Assert.IsTrue(runResponse.InputTokens.HasValue, "Input tokens should be tracked");
        Assert.IsTrue(runResponse.OutputTokens.HasValue, "Output tokens should be tracked");
        Assert.IsGreaterThan(0L, runResponse.ElapsedMs, "Elapsed time should be positive");
    }

    /// <summary>
    /// Test that /v1/run endpoint returns error response when providerId is nonexistent.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RunEndpoint_WithInvalidProviderId_ReturnsError()
    {
        await using var runtime = await HttpRuntimeFixture.StartAsync(configured: false);
        using var client = new HttpClient
        {
            BaseAddress = runtime.Address,
            Timeout = TimeSpan.FromSeconds(15)
        };

        var request = new HttpRunRequest
        {
            ModelId = "test-model",
            Input = "Test input",
            ProviderId = "nonexistent",
            RequestControl = new ExpertRequestControl
            {
                MaxTokens = 100,
                Temperature = 0.7
            }
        };

        // Act: Call /v1/run endpoint with invalid providerId
        var response = await client.PostAsJsonAsync("/v1/run", request);

        // Assert: Verify error response
        Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode, "Should return error status code for invalid providerId");

        var content = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(content), "Error response should contain content");

        // Verify the response contains error information
        Assert.IsTrue(
            content.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("nonexistent", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("error", StringComparison.OrdinalIgnoreCase),
            "Error response should mention provider issue");
    }

    /// <summary>
    /// Test that /v1/run endpoint validates temperature parameter and rejects values > 2.0.
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task RunEndpoint_WithInvalidTemperature_ReturnsValidationError()
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
            Input = "Test input",
            ProviderId = "test-provider",
            RequestControl = new ExpertRequestControl
            {
                Temperature = 3.0  // Invalid: > 2.0
            }
        };

        // Act: Call /v1/run endpoint with invalid temperature
        var response = await client.PostAsJsonAsync("/v1/run", request);

        // Assert: Verify validation error response
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
            "Should return 400 Bad Request for invalid temperature");

        var content = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(content), "Error response should contain content");

        // Verify the response contains temperature validation error
        Assert.IsTrue(
            content.Contains("Temperature", StringComparison.OrdinalIgnoreCase) &&
            (content.Contains("2.0", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("must be", StringComparison.OrdinalIgnoreCase)),
            "Error response should mention temperature validation constraint");
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
