using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.OpenAI.Image.Cli;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class ApiEndpointTests
{
    [TestMethod]
    [DataRow("http://127.0.0.1:3425/v1")]
    [DataRow("http://127.0.0.2:3425/v1/")]
    [DataRow("http://localhost:3425/v1")]
    [DataRow("http://LOCALHOST:3425/v1")]
    [DataRow("http://loopback:3425/v1")]
    [DataRow("http://[::1]:3425/v1")]
    [DataRow("https://api.example.test/v1")]
    public async Task GenerateAcceptsHttpsOrLoopbackHttpAndWritesArtifact(string apiUrl)
    {
        using var workspace = new TestWorkspace();
        Uri? endpoint = null;
        string? authorization = null;
        using var client = new HttpClient(new Handler(message =>
        {
            endpoint = message.RequestUri;
            authorization = message.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(ImageFixture.Create()) + "\"}]}", Encoding.UTF8, "application/json")
            };
        }));
        using var output = new StringWriter();

        var code = await new OpenAiImageApplication(client).RunAsync(
            ["generate", "--request-id", "endpoint", "--prompt", "hello", "--api-key", "test-secret", "--api-url", apiUrl, "--output-dir", "output"], output, workspace.Root);

        Assert.AreEqual(0, code);
        Assert.AreEqual(new Uri(apiUrl.TrimEnd('/') + "/images/generations"), endpoint);
        Assert.AreEqual("Bearer test-secret", authorization);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual("endpoint", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("image_001.png", response["artifacts"]![0]!["path"]!.GetValue<string>());
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Root, "output", "image_001.png")));
        Assert.DoesNotContain(output.ToString(), "test-secret");
    }

    [TestMethod]
    public async Task TryRunAcceptsLoopbackHttpWithoutNetworkOrOutputDirectory()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Try-run must not access the network.")));
        using var output = new StringWriter();

        var code = await new OpenAiImageApplication(client).RunAsync(
            ["generate", "--request-id", "endpoint-preview", "--prompt", "hello", "--api-key", "test-secret", "--api-url", "http://127.0.0.1:3425/v1", "--output-dir", "output", "--try-run"], output, workspace.Root);

        Assert.AreEqual(0, code);
        Assert.AreEqual("validated", JsonNode.Parse(output.ToString())!["status"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "output")));
        Assert.DoesNotContain(output.ToString(), "test-secret");
    }

    [TestMethod]
    [DataRow("http://api.example.test/v1")]
    [DataRow("http://192.168.1.2:3425/v1")]
    [DataRow("http://0.0.0.0:3425/v1")]
    [DataRow("http://localhost.example.test:3425/v1")]
    [DataRow("http://127.0.0.1.example.test:3425/v1")]
    [DataRow("http://[2001:db8::1]:3425/v1")]
    [DataRow("http://loopback.example.test:3425/v1")]
    [DataRow("ftp://127.0.0.1/v1")]
    [DataRow("/v1")]
    [DataRow("http://user:password@127.0.0.1:3425/v1")]
    [DataRow("http://127.0.0.1:3425/v1?token=value")]
    [DataRow("http://127.0.0.1:3425/v1#fragment")]
    [DataRow("https://user:password@api.example.test/v1")]
    [DataRow("https://api.example.test/v1?token=value")]
    [DataRow("https://api.example.test/v1#fragment")]
    public async Task InvalidEndpointFailsBeforeNetworkOrOutputDirectory(string apiUrl)
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Invalid endpoints must not access the network.")));
        using var output = new StringWriter();

        var code = await new OpenAiImageApplication(client).RunAsync(
            ["generate", "--request-id", "endpoint-invalid", "--prompt", "hello", "--api-key", "test-secret", "--api-url", apiUrl, "--output-dir", "output"], output, workspace.Root);

        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(1, code);
        Assert.AreEqual("endpoint-invalid", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("invalid_parameter", response["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "output")));
        Assert.DoesNotContain(output.ToString(), "test-secret");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
