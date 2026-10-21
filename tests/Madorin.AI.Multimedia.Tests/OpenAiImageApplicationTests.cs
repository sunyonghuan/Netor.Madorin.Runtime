using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.OpenAI.Image.Cli;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class OpenAiImageApplicationTests
{
    private static string ImageBase64 => Convert.ToBase64String(ImageFixture.Create());

    [TestMethod]
    public async Task TryRunPreservesChineseAndUsesFileRelativeOutputWithoutSideEffects()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "requests"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "requests/job.json"), """
            {"schema_version":"1.0","request_id":"req-file","task":"text-to-image","prompt":"水墨山水",
             "parameters":{"quality":"low","unknown":{"private":"value"}},
             "execution":{"try_run":true,"output_dir":"results"}}
            """);
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Try-run must not access the network.")));
        using var output = new StringWriter();
        var code = await new OpenAiImageApplication(client).RunAsync(["generate", "--request-file", "requests/job.json", "--api-key", "test-secret", "--quality", "high"], output, workspace.Root);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(0, code);
        Assert.AreEqual("req-file", json["response_id"]!.GetValue<string>());
        Assert.AreEqual("水墨山水", json["request_preview"]!["prompt"]!.GetValue<string>());
        Assert.AreEqual("high", json["request_preview"]!["quality"]!.GetValue<string>());
        Assert.AreEqual("results", json["effective_parameters"]!["execution"]!["output_dir"]!.GetValue<string>());
        Assert.AreEqual("/parameters/unknown", json["ignored_parameters"]![0]!["parameter"]!.GetValue<string>());
        Assert.DoesNotContain(output.ToString(), "test-secret");
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "requests/results")));
    }

    [TestMethod]
    public async Task GenerateUsesCorrectEndpointAndPreservesExistingArtifact()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "image_001.png"), "existing");
        Uri? endpoint = null;
        string? body = null;
        using var client = new HttpClient(new FakeHandler(async message =>
        {
            endpoint = message.RequestUri;
            body = await message.Content!.ReadAsStringAsync();
            return Success("{\"data\":[{\"b64_json\":\"" + ImageBase64 + "\"}]}");
        }));
        using var output = new StringWriter();
        var code = await new OpenAiImageApplication(client).RunAsync(["generate", "--request-id", "req-image", "--prompt", "hello", "--api-key", "key", "--output-dir", "output"], output, workspace.Root);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(0, code);
        Assert.AreEqual("https://api.openai.com/v1/images/generations", endpoint!.AbsoluteUri);
        Assert.AreEqual("png", JsonNode.Parse(body!)!["output_format"]!.GetValue<string>());
        Assert.AreEqual("image_002.png", json["artifacts"]![0]!["path"]!.GetValue<string>());
        Assert.AreEqual(2, json["artifacts"]![0]!["width"]!.GetValue<int>());
        Assert.AreEqual("existing", await File.ReadAllTextAsync(Path.Combine(directory, "image_001.png")));
    }

    [TestMethod]
    public async Task FailedBatchRollsBackOnlyFilesCreatedByInvocation()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(Success("{\"data\":[{\"b64_json\":\"" + ImageBase64 + "\"},{\"b64_json\":\"invalid\"}]}"))));
        using var output = new StringWriter();
        var code = await new OpenAiImageApplication(client).RunAsync(["generate", "--request-id", "req-batch", "--prompt", "hello", "--api-key", "key", "--output-dir", "output", "--num-images", "2"], output, workspace.Root);
        Assert.AreEqual(125, code);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(workspace.Root, "output")));
    }

    [TestMethod]
    public async Task ConcurrentInvocationsKeepResponseAndProviderIdsIsolated()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(async message =>
        {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync())!;
            await Task.Yield();
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"code\":\"invalid_api_key\"}}") };
            response.Headers.Add("x-request-id", body["prompt"]!.GetValue<string>());
            return response;
        }));
        var app = new OpenAiImageApplication(client);
        async Task<JsonNode> InvokeAsync(string id)
        {
            using var output = new StringWriter();
            var code = await app.RunAsync(["generate", "--request-id", id, "--prompt", id, "--api-key", "key", "--output-dir", "output"], output, workspace.Root);
            Assert.AreEqual(2, code);
            return JsonNode.Parse(output.ToString())!;
        }

        var responses = await Task.WhenAll(InvokeAsync("req-a"), InvokeAsync("req-b"));
        foreach (var response in responses)
        {
            Assert.AreEqual(response["response_id"]!.GetValue<string>(), response["provider_metadata"]!["request_id"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task PrequeuedCancellationStopsBeforeProviderCall()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Cancelled calls must not reach the provider.")));
        using var control = new MemoryStream(Encoding.UTF8.GetBytes("{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"req-cancel\"}\n"));
        using var output = new StringWriter();
        var code = await new OpenAiImageApplication(client).RunAsync(
            ["generate", "--request-id", "req-cancel", "--prompt", "hello", "--api-key", "key", "--output-dir", "output"], output, workspace.Root, control);
        Assert.AreEqual(130, code);
        Assert.AreEqual("cancelled", JsonNode.Parse(output.ToString())!["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "output")));
    }

    [TestMethod]
    public async Task MalformedSupportedFileParameterReturnsCorrelatedValidationError()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "job.json"), """
            {"schema_version":"1.0","request_id":"req-invalid","task":"text-to-image","prompt":"hello","parameters":{"num_images":"two"}}
            """);
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Validation must not access the provider.")));
        using var output = new StringWriter();
        var code = await new OpenAiImageApplication(client).RunAsync(["generate", "--request-file", "job.json", "--try-run", "--api-key", "key"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(1, code);
        Assert.AreEqual("req-invalid", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("invalid", response["status"]!.GetValue<string>());
    }

    private static HttpResponseMessage Success(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
