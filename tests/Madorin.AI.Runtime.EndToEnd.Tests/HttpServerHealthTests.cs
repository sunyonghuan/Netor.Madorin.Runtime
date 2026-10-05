using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Madorin.AI.Runtime.Server.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public class HttpServerHealthTests
{
    [TestMethod]
    public async Task HealthEndpoint_ReturnsHealthyStatus()
    {
        var port = GetAvailablePort();
        var pipeName = $"test-pipe-{Guid.NewGuid():N}";
        var instanceId = Guid.NewGuid().ToString("N");
        var handshakeSecret = Guid.NewGuid().ToString("N");

        await using var server = new RuntimeHttpServer(
            "localhost",
            port,
            pipeName,
            instanceId,
            handshakeSecret,
            NullLoggerFactory.Instance);

        await server.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{port}/v1/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var statusProperty = json.RootElement.GetProperty("status");
        Assert.AreEqual("healthy", statusProperty.GetString());
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
