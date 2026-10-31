using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Cli;

namespace Madorin.AI.Multimedia.Tests;

public sealed partial class GrokVideoApplicationTests
{
    private static readonly string[] PlaceholderCredential = ["--api-key", "placeholder"];

    [TestMethod]
    public async Task TryRunStillRequiresCredentialAndAcceptsPlaceholder()
    {
        using var workspace = new TestWorkspace();
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(_ => { calls++; throw new AssertFailedException("预检不能联网。"); }));
        foreach (var supplied in new[] { false, true })
        {
            using var output = new StringWriter();
            var code = await new GrokVideoApplication(client).RunAsync(
                ["generate", "--request-id", "req-key", "--prompt", "水墨山水", "--try-run",
                    .. supplied ? PlaceholderCredential : []], output, workspace.Root);
            var response = JsonNode.Parse(output.ToString())!;
            Assert.AreEqual(supplied ? 0 : 1, code);
            Assert.AreEqual(supplied ? "validated" : "invalid", response["status"]!.GetValue<string>());
            Assert.AreEqual(supplied, response["credential_presence"]!["api_key"]!.GetValue<bool>());
            if (supplied)
            {
                Assert.AreEqual(300, response["effective_parameters"]!["execution"]!["timeout"]!.GetValue<int>());
                Assert.AreEqual("水墨山水", response["request_preview"]!["prompt"]!.GetValue<string>());
                Assert.IsNull(response["input_parameters"]!["model"]);
            }
        }
        Assert.AreEqual(0, calls);
        Assert.IsEmpty(Directory.GetFiles(workspace.Root));
    }

    [TestMethod]
    [DataRow("--image-url", "../outside.png")]
    [DataRow("--attachment", "{\"id\":\"a\",\"id\":\"b\",\"role\":\"source\",\"source\":{\"path\":\"missing.png\"}}")]
    [DataRow("--attachment", "{\"id\":\"a\",\"role\":\"source\",\"extra\":true,\"source\":{\"path\":\"missing.png\"}}")]
    [DataRow("--attachment", "{\"id\":\"a\",\"role\":\"source\",\"source\":{\"path\":\"missing.png\",\"extra\":true}}")]
    public async Task UnsupportedAttachmentsStillValidateStructureAndPaths(string option, string value)
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("预检不能联网。")));
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client).RunAsync(
            ["generate", "--request-id", "req-attachment", "--prompt", "river", "--api-key", "placeholder", "--try-run", option, value],
            output, workspace.Root);
        Assert.AreEqual(1, code);
        Assert.AreEqual("invalid", JsonNode.Parse(output.ToString())!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task IgnoredAttachmentAndParametersDoNotLeakTheirValues()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("预检不能联网。")));
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client).RunAsync(
            ["generate", "--request-id", "req-ignored", "--prompt", "river", "--api-key", "placeholder", "--try-run",
                "--image-url", "https://cdn.example.test/image.png?signature=hidden-secret", "--style", "hidden-style"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(0, code);
        Assert.AreEqual(2, response["ignored_parameters"]!.AsArray().Count);
        Assert.IsTrue(response["ignored_parameters"]!.AsArray().Any(item => item!["parameter"]!.GetValue<string>() == "/attachments/0"));
        Assert.DoesNotContain(output.ToString(), "hidden-secret");
        Assert.DoesNotContain(output.ToString(), "hidden-style");
    }

    [TestMethod]
    public async Task MetadataDeclaresEveryAcceptedCompatibilityOption()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("metadata is offline.")));
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client).RunAsync(
            ["metadata", "--request-id", "req-metadata"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        var generate = response["commands"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "generate");
        var declared = generate!["parameters"]!.AsArray()
            .Select(parameter => parameter!["cli_name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var accepted = new[]
        {
            "--request-id", "--request-file", "--api-url", "--api-key", "--output-dir", "--task", "--model", "--prompt",
            "--duration", "--aspect-ratio", "--resolution", "--output-format", "--timeout", "--try-run", "--verbose", "--no-wait",
            "--enable-audio", "--fps", "--image-url", "--first-frame-url", "--last-frame-url", "--multi-shot", "--width", "--height",
            "--size", "--quality", "--seed", "--style", "--negative-prompt", "--num-images", "--reference-weight", "--guidance-scale", "--steps"
        };

        Assert.AreEqual(0, code);
        CollectionAssert.AreEquivalent(accepted, declared.ToArray());
    }

    [TestMethod]
    [DataRow("https://example.test/content?token=hidden-token")]
    [DataRow("参考这张图片 https://example.test/content?token=hidden-token 生成视频")]
    public async Task SignedUriIsRedactedFromEveryTryRunView(string prompt)
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("预检不能联网。")));
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client).RunAsync(
            ["generate", "--request-id", "req-private", "--prompt", prompt,
                "--api-key", "hidden-key", "--try-run"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(0, code);
        Assert.AreEqual("<redacted>", response["request_preview"]!["prompt"]!.GetValue<string>());
        Assert.DoesNotContain(output.ToString(), "hidden-token");
        Assert.DoesNotContain(output.ToString(), "hidden-key");
        Assert.IsFalse(response["unicode_checks"]!.AsArray().Any(item => item!["parameter"]!.GetValue<string>() == "/prompt"));
    }

    [TestMethod]
    public async Task ExplicitModelOverridesInvalidFileModelWithoutWhitelist()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), "{\"schema_version\":\"1.0\",\"model\":42,\"prompt\":\"river\"}");
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("预检不能联网。")));
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client).RunAsync(
            ["generate", "--request-id", "req-model", "--request-file", "request.json", "--model", "custom-model-2026",
                "--api-key", "placeholder", "--try-run"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(0, code);
        Assert.AreEqual("custom-model-2026", response["model"]!.GetValue<string>());
        Assert.AreEqual("custom-model-2026", response["request_preview"]!["model"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task QueryNetworkFailureKeepsValidatedContext()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-network");
        using var client = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("hidden-transport-details")));
        var (code, response) = await RunQueryAsync(workspace, client, "vid-network");
        Assert.AreEqual(6, code);
        Assert.AreEqual("vid-network", response["task_id"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
        Assert.DoesNotContain(response.ToJsonString(), "hidden-transport-details");
    }

    [TestMethod]
    public async Task PendingQueryStopsAtTotalTimeout()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-timeout");
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(Success("{\"status\":\"pending\"}"))));
        using var output = new StringWriter();
        var code = await new GrokVideoApplication(client, TimeSpan.FromSeconds(5)).RunAsync(
            ["query", "--request-id", "req-timeout", "--task-id", "vid-timeout", "--task-context-file", "context.json",
                "--api-key", "placeholder", "--timeout", "1", "--wait"], output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        Assert.AreEqual(4, code);
        Assert.AreEqual("timeout", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("pending", response["provider_metadata"]!["remote_status"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
    }

    [TestMethod]
    public async Task JsonResponseLimitAndUtf8AreEnforced()
    {
        foreach (var content in new[] { new byte[] { 0xFF }, Encoding.UTF8.GetBytes(new string(' ', 4 * 1024 * 1024 + 1)) })
        {
            using var workspace = new TestWorkspace();
            await WriteContextAsync(workspace, "vid-body");
            using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new MemoryStream(content)) })));
            var (code, response) = await RunQueryAsync(workspace, client, "vid-body");
            Assert.AreEqual(125, code);
            Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
            Assert.IsNotNull(response["task_context"]);
        }
    }
}
