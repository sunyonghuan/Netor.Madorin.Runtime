using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Image.Cli;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class GrokImageApplicationTests
{
    [TestMethod]
    public async Task GenerateMapsGrokRequestAndWritesVerifiedArtifact()
    {
        using var workspace = new TestWorkspace();
        Uri? endpoint = null;
        string? authorization = null;
        JsonNode? request = null;
        using var client = new HttpClient(new FakeHandler(async message =>
        {
            endpoint = message.RequestUri;
            authorization = message.Headers.Authorization?.ToString();
            request = JsonNode.Parse(await message.Content!.ReadAsStringAsync());
            return Success("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(ImageFixture.Create()) + "\",\"mime_type\":\"image/png\"}]}");
        }));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-grok", "--prompt", "A blue bird", "--api-key", "secret",
             "--api-url", "https://example.test/v1", "--model", "grok-imagine-image-2.0", "--aspect-ratio", "16:9", "--num-images", "1", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("https://example.test/v1/images/generations", endpoint!.AbsoluteUri);
        Assert.AreEqual("Bearer secret", authorization);
        Assert.AreEqual("grok-imagine-image-2.0", request!["model"]!.GetValue<string>());
        Assert.AreEqual("A blue bird", request["prompt"]!.GetValue<string>());
        Assert.AreEqual(1, request["n"]!.GetValue<int>());
        Assert.AreEqual("16:9", request["aspect_ratio"]!.GetValue<string>());
        Assert.AreEqual("b64_json", request["response_format"]!.GetValue<string>());
        Assert.AreEqual("req-grok", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("image_001.png", response["artifacts"]![0]!["path"]!.GetValue<string>());
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Root, "out", "image_001.png")));
        Assert.DoesNotContain(output.ToString(), "secret");
    }

    [TestMethod]
    public async Task GenerateAcceptsLoopbackHttpAndDetectsPngWithoutMime()
    {
        using var workspace = new TestWorkspace();
        Uri? endpoint = null;
        using var client = new HttpClient(new FakeHandler(async message =>
        {
            endpoint = message.RequestUri;
            var encoded = Convert.ToBase64String(ImageFixture.Create());
            return Success("{\"data\":[{\"b64_json\":\"" + encoded + "\"}]}");
        }));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-loopback", "--prompt", "simple", "--api-key", "secret",
             "--api-url", "http://127.0.0.1:3425/v1", "--output-dir", "out"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("http://127.0.0.1:3425/v1/images/generations", endpoint!.AbsoluteUri);
        Assert.AreEqual("image_001.png", response["artifacts"]![0]!["path"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UrlOnlyProviderResponseIsRejectedWithoutWritingArtifacts()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(
            Success("{\"data\":[{\"url\":\"https://cdn.example.test/image.png\"}]}"))));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-url", "--prompt", "simple", "--api-key", "secret", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(125, code);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }

    [TestMethod]
    public async Task DeferredProviderResponseIsRejectedAsUnsupportedAsync()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"request_id\":\"remote-1\",\"status\":\"pending\"}", Encoding.UTF8, "application/json")
            })));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-deferred", "--prompt", "simple", "--api-key", "secret", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(125, code);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }

    [TestMethod]
    public async Task TryRunUsesExplicitModelAndDoesNotReachProvider()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "job.json"), """
            {"schema_version":"1.0","task":"text-to-image","model":"grok-imagine-image-2.0","prompt":"水墨山水","parameters":{"size":"1024x1024","num_images":1}}
            """);
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("try-run must not access the provider")));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-file", "job.json", "--request-id", "req-try-run", "--model", "grok-imagine-image-2.0",
             "--api-key", "secret", "--try-run=true", "--verbose=true"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
        Assert.AreEqual("grok-imagine-image-2.0", response["request_preview"]!["model"]!.GetValue<string>());
        Assert.AreEqual("grok-imagine-image-2.0", response["effective_parameters"]!["model"]!.GetValue<string>());
        Assert.AreEqual("/parameters/size", response["ignored_parameters"]![0]!["parameter"]!.GetValue<string>());
        Assert.IsTrue(response["effective_parameters"]!["execution"]!["verbose"]!.GetValue<bool>());
        Assert.DoesNotContain(output.ToString(), "secret");
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }

    [TestMethod]
    public async Task UnsupportedModelReturnsCorrelatedValidationFailure()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("invalid models must not access the provider")));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-invalid-model", "--prompt", "hello", "--api-key", "secret",
             "--model", "unsupported-model", "--try-run"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(1, code);
        Assert.AreEqual("req-invalid-model", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("invalid", response["status"]!.GetValue<string>());
        Assert.DoesNotContain(output.ToString(), "secret");
    }

    [TestMethod]
    public async Task OfficialImageCountLimitAllowsTenImagesInTryRun()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("try-run must not access the provider")));
        using var output = new StringWriter();

        var code = await new GrokImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-ten", "--prompt", "simple", "--api-key", "secret",
             "--num-images", "10", "--try-run"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual(10, response["effective_parameters"]!["parameters"]!["num_images"]!.GetValue<int>());
    }

    private static HttpResponseMessage Success(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
