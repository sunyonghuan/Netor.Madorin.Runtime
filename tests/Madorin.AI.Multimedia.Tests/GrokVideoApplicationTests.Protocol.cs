using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Cli;

namespace Madorin.AI.Multimedia.Tests;

public sealed partial class GrokVideoApplicationTests
{
    [TestMethod]
    public async Task PendingQueryRestoresModelAndCompleteContext()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-context");
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(Success("{\"status\":\"pending\"}"))));
        var (code, response) = await RunQueryAsync(workspace, client, "vid-context");

        Assert.AreEqual(0, code);
        Assert.AreEqual("pending", response["status"]!.GetValue<string>());
        Assert.AreEqual("grok-imagine-video-1.5", response["task_context"]!["model"]!.GetValue<string>());
        Assert.AreEqual(1, response["task_context"]!["output_indices"]![0]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{\"status\":\"pending\",\"status\":\"done\"}")]
    [DataRow("{\"status\":\"done\"}")]
    [DataRow("{\"status\":\"done\",\"video\":{\"url\":\"http://cdn.example.test/video.mp4\"}}")]
    [DataRow("{\"status\":\"done\",\"video\":{\"url\":\"https://cdn.example.test/video.mp4\",\"duration\":\"bad\"}}")]
    [DataRow("{\"status\":\"pending\",\"model\":42}")]
    public async Task InvalidProviderResponsePreservesKnownTask(string body)
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-invalid");
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(Success(body))));
        var (code, response) = await RunQueryAsync(workspace, client, "vid-invalid");

        Assert.AreEqual(125, code);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("vid-invalid", response["task_id"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
        Assert.IsFalse(response["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task TaskIdIsEscapedAsOnePathSegment()
    {
        using var workspace = new TestWorkspace();
        const string taskId = "video/part?x=1#fragment";
        await WriteContextAsync(workspace, taskId);
        Uri? requested = null;
        using var client = new HttpClient(new FakeHandler(message =>
        {
            requested = message.RequestUri;
            return Task.FromResult(Success("{\"status\":\"pending\"}"));
        }));
        var (code, _) = await RunQueryAsync(workspace, client, taskId);

        Assert.AreEqual(0, code);
        Assert.AreEqual("/v1/videos/video%2Fpart%3Fx%3D1%23fragment", requested!.AbsolutePath);
        Assert.AreEqual(string.Empty, requested.Query);
        Assert.AreEqual(string.Empty, requested.Fragment);
    }

    [TestMethod]
    [DataRow("query", "insufficient_quota", "insufficient_balance")]
    [DataRow("generate", "insufficient_quota", "insufficient_balance")]
    [DataRow("download", "content_policy_violation", "content_policy_violation")]
    public async Task RemoteFailureUsesStableErrorAndKeepsContext(string command, string providerCode, string expected)
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-failed");
        using var client = new HttpClient(new FakeHandler(message => Task.FromResult(
            message.Method == HttpMethod.Post ? Success("{\"request_id\":\"vid-failed\"}") :
            Success(new JsonObject { ["status"] = "failed", ["error"] = new JsonObject { ["code"] = providerCode } }.ToJsonString()))));
        using var output = new StringWriter();
        string[] args = command == "generate"
            ? ["generate", "--request-id", "req-failed", "--prompt", "river", "--api-key", "placeholder", "--output-dir", "out"]
            : [command, "--request-id", "req-failed", "--task-id", "vid-failed", "--task-context-file", "context.json",
                "--api-key", "placeholder", .. command == "download" ? new[] { "--output-dir", "out" } : []];
        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(args, output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(3, code);
        Assert.AreEqual(expected, response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("vid-failed", response["task_id"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
        Assert.IsFalse(response["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }

    [TestMethod]
    [DataRow(401, "authentication_failed")]
    [DataRow(404, "provider_error")]
    [DataRow(429, "rate_limit_exceeded")]
    public async Task HttpFailureKeepsTaskAndProviderTracking(int status, string expected)
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-http");
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            var reply = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":\"not-an-object\"}") };
            reply.Headers.Add("x-request-id", "provider-trace");
            return Task.FromResult(reply);
        }));
        var (_, response) = await RunQueryAsync(workspace, client, "vid-http");

        Assert.AreEqual(expected, response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("vid-http", response["task_id"]!.GetValue<string>());
        Assert.AreEqual("provider-trace", response["provider_metadata"]!["request_id"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
    }

    [TestMethod]
    public async Task ProviderTraceAndErrorCodesNeverEchoApiKey()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-secret-echo");
        const string apiKey = "sensitive-api-key";
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            var reply = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"code\":\"" + apiKey + "\"}}")
            };
            reply.Headers.Add("x-request-id", apiKey);
            return Task.FromResult(reply);
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client).RunAsync(
            ["query", "--request-id", "req-secret-echo", "--task-id", "vid-secret-echo", "--task-context-file", "context.json",
                "--api-key", apiKey, "--api-url", "https://example.test/v1"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(2, code);
        Assert.IsNull(response["provider_metadata"]!["request_id"]);
        Assert.IsNull(response["error"]!["provider_error"]);
        Assert.DoesNotContain(output.ToString(), apiKey);
    }

    private static async Task<(int Code, JsonNode Response)> RunQueryAsync(TestWorkspace workspace, HttpClient client, string taskId, params string[] extra)
    {
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["query", "--request-id", "req-query", "--task-id", taskId, "--task-context-file", "context.json", "--api-key", "placeholder",
                "--api-url", "https://example.test/v1", .. extra], output, workspace.Root);
        return (code, JsonNode.Parse(output.ToString())!);
    }
}
