using System.Net;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Image.Cli;
using Madorin.AI.OpenAI.Image.Cli;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class ModelDiscoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ModelsComeFromSelectedApiAndAreNotFilteredByLocalNames(bool grok)
    {
        using var workspace = new TestWorkspace();
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://gateway.example.test/custom/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer secret", request.Headers.Authorization!.ToString());
            var body = calls == 1 ? """{"data":[{"id":"gpt-image-2"},{"id":"deployment/custom"},{"id":"text-model"}]}""" : """{"data":[{"id":"new-deployment"}]}""";
            return Task.FromResult(Reply(HttpStatusCode.OK, body));
        }));
        for (var index = 0; index < 2; index++)
        {
            var (code, response, raw) = await Invoke(grok, client, workspace,
                ["list-models", "--request-id", "models-" + index, "--api-url", "https://gateway.example.test/custom/v1", "--api-key", "secret"]);
            Assert.AreEqual(0, code);
            Assert.AreEqual("models-" + index, response["response_id"]!.GetValue<string>());
            Assert.AreEqual("api", response["model_source"]!.GetValue<string>());
            Assert.AreEqual(index == 0 ? 3 : 1, response["models"]!.AsArray().Count);
            Assert.AreEqual(index == 0 ? "gpt-image-2" : "new-deployment", response["models"]![0]!["id"]!.GetValue<string>());
            Assert.IsNull(response["models"]![0]!["tasks"]);
            Assert.DoesNotContain(raw, "secret");
        }
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MetadataIsOfflineAndDeclaresAuthenticatedDiscovery(bool grok)
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("metadata must be offline")));
        var (code, response, _) = await Invoke(grok, client, workspace, ["metadata", "--request-id", "meta"]);
        Assert.AreEqual(0, code);
        Assert.HasCount(0, response["models"]!.AsArray());
        Assert.HasCount(1, response["tasks"]!.AsArray());
        Assert.IsNull(response["tasks"]![0]!["validation_schema"]!["properties"]!["model"]!["const"]);
        var command = response["commands"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "list-models")!;
        Assert.IsTrue(command["authentication_required"]!.GetValue<bool>());
        Assert.IsTrue(command["parameters"]!.AsArray().Any(x => x!["cli_name"]!.GetValue<string>() == "--api-url"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NewModelGeneratesWithoutDiscoveryPrecondition(bool grok)
    {
        using var workspace = new TestWorkspace();
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, cancellation) =>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/v1/images/generations", request.RequestUri!.AbsolutePath);
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;
            Assert.AreEqual("deployment/future-image", payload["model"]!.GetValue<string>());
            return Reply(HttpStatusCode.OK, "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(ImageFixture.Create()) + "\"}]}");
        }));
        var (code, response, _) = await Invoke(grok, client, workspace,
            ["generate", "--request-id", "new-model", "--model", "deployment/future-image", "--prompt", "hello", "--api-key", "secret", "--output-dir", "out"]);
        Assert.AreEqual(0, code);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("deployment/future-image", response["provider_metadata"]!["model"]!.GetValue<string>());
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Root, "out", "image_001.png")));
    }

    [TestMethod]
    [DataRow(false, 401, "{\"error\":{\"code\":\"invalid_api_key\"}}", "authentication_failed")]
    [DataRow(true, 404, "{}", "provider_error")]
    [DataRow(false, 200, "{\"data\":[{\"id\":\"same\"},{\"id\":\"same\"}]}", "provider_error")]
    [DataRow(true, 200, "{\"data\":[{\"id\":1}]}", "provider_error")]
    [DataRow(false, 200, "not-json", "provider_error")]
    public async Task DiscoveryFailureDoesNotFallBackToEmbeddedModels(bool grok, int status, string body, string expected)
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Reply((HttpStatusCode)status, body))));
        var (code, response, raw) = await Invoke(grok, client, workspace,
            ["list-models", "--request-id", "failed-models", "--api-key", "secret"]);
        Assert.AreNotEqual(0, code);
        Assert.AreEqual("failed-models", response["response_id"]!.GetValue<string>());
        Assert.AreEqual(expected, response["error"]!["code"]!.GetValue<string>());
        Assert.IsNull(response["models"]);
        Assert.DoesNotContain(raw, "secret");
    }

    [TestMethod]
    public async Task EmptyCatalogIsAnEmptyRemoteResult()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, "{\"data\":[]}"))));
        var (code, response, _) = await Invoke(false, client, workspace, ["list-models", "--request-id", "empty", "--api-key", "secret"]);
        Assert.AreEqual(0, code);
        Assert.HasCount(0, response["models"]!.AsArray());
    }

    [TestMethod]
    public async Task DiscoveryHonorsTotalTimeout()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return Reply(HttpStatusCode.OK, "{}");
        }));
        var (code, response, _) = await Invoke(false, client, workspace,
            ["list-models", "--request-id", "timeout", "--api-key", "secret", "--timeout", "1"]);
        Assert.AreEqual(4, code);
        Assert.AreEqual("timeout", response["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteModelRejectionDoesNotChooseAnotherModel(bool grok)
    {
        using var workspace = new TestWorkspace();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"model_not_found\"}}"));
        }));
        var (code, response, _) = await Invoke(grok, client, workspace,
            ["generate", "--request-id", "remote-rejected", "--model", "unavailable/deployment", "--prompt", "hello", "--api-key", "secret", "--output-dir", "out"]);
        Assert.AreEqual(125, code);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("unavailable/deployment", response["provider_metadata"]!["model"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }

    [TestMethod]
    public async Task MissingDiscoveryCredentialFailsWithoutNetworking()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("missing key")));
        var (code, response, _) = await Invoke(false, client, workspace, ["list-models", "--request-id", "no-key"]);
        Assert.AreEqual(1, code);
        Assert.AreEqual("invalid_parameter", response["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DiscoveryUsesInvocationCancellationChannel()
    {
        using var workspace = new TestWorkspace();
        using var control = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"cancel-models\"}\n"));
        using var client = new HttpClient(new Handler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return Reply(HttpStatusCode.OK, "{}");
        }));
        using var output = new StringWriter();
        var code = await new OpenAiImageApplication(client).RunAsync(
            ["list-models", "--request-id", "cancel-models", "--api-key", "secret"], output, workspace.Root, control);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(130, code);
        Assert.AreEqual("cancelled", response["error"]!["code"]!.GetValue<string>());
    }

    private static async Task<(int Code, JsonNode Response, string Raw)> Invoke(bool grok, HttpClient client, TestWorkspace workspace, string[] args)
    {
        using var output = new StringWriter();
        var code = grok
            ? await new GrokImageApplication(client).RunAsync(args, output, workspace.Root)
            : await new OpenAiImageApplication(client).RunAsync(args, output, workspace.Root);
        return (code, JsonNode.Parse(output.ToString())!, output.ToString());
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
