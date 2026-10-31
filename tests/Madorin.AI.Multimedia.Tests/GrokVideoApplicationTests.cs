using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Cli;
using Madorin.AI.Grok.Video.Provider;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed partial class GrokVideoApplicationTests
{
    private static byte[] Mp4 = [];

    [ClassInitialize]
    public static async Task CreateVideoFixture(TestContext context)
    {
        _ = context;
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "fixture.mp4");
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1", "-t", "1", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", path })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        Assert.AreEqual(0, process.ExitCode, await errors);
        Mp4 = await File.ReadAllBytesAsync(path);
    }

    [TestMethod]
    public async Task NoWaitSubmitsAndReturnsTaskWithoutDownloading()
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
            return Success("{\"request_id\":\"vid-1\"}");
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["generate", "--request-id", "req-video", "--prompt", "A red apple rolling", "--api-key", "secret",
             "--api-url", "https://example.test/v1", "--model", "grok-imagine-video-1.5", "--duration", "6",
             "--aspect-ratio", "1:1", "--resolution", "720p", "--no-wait"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("https://example.test/v1/videos/generations", endpoint!.AbsoluteUri);
        Assert.AreEqual("Bearer secret", authorization);
        Assert.AreEqual("grok-imagine-video-1.5", request!["model"]!.GetValue<string>());
        Assert.AreEqual(6, request["duration"]!.GetValue<int>());
        Assert.AreEqual("1:1", request["aspect_ratio"]!.GetValue<string>());
        Assert.AreEqual("720p", request["resolution"]!.GetValue<string>());
        Assert.AreEqual("submitted", response["status"]!.GetValue<string>());
        Assert.AreEqual("vid-1", response["task_id"]!.GetValue<string>());
        Assert.AreEqual("req-video", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("vid-1", response["task_context"]!["task_id"]!.GetValue<string>());
        Assert.DoesNotContain(output.ToString(), "secret");
    }

    [TestMethod]
    public async Task GenerateWaitsThenDownloadsMp4()
    {
        using var workspace = new TestWorkspace();
        var polls = 0;
        using var client = new HttpClient(new FakeHandler(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                return Task.FromResult(Success("{\"request_id\":\"vid-2\"}"));
            }

            if (message.RequestUri!.AbsolutePath.EndsWith("/videos/vid-2", StringComparison.Ordinal))
            {
                polls++;
                var body = polls == 1
                    ? "{\"status\":\"pending\"}"
                    : "{\"status\":\"done\",\"model\":\"grok-imagine-video-1.5\",\"video\":{\"url\":\"https://cdn.example.test/video.mp4\",\"duration\":6}}";
                return Task.FromResult(Success(body));
            }

            Assert.AreEqual("https://cdn.example.test/video.mp4", message.RequestUri.AbsoluteUri);
            Assert.IsNull(message.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp4) });
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["generate", "--request-id", "req-wait", "--prompt", "rolling apple", "--api-key", "secret",
             "--api-url", "https://example.test/v1", "--duration", "6", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("success", response["status"]!.GetValue<string>());
        Assert.AreEqual("video_001.mp4", response["artifacts"]![0]!["path"]!.GetValue<string>());
        Assert.IsGreaterThan(0, response["artifacts"]![0]!["duration_seconds"]!.GetValue<double>());
        Assert.AreEqual(64, response["artifacts"]![0]!["width"]!.GetValue<int>());
        Assert.IsFalse(response["artifacts"]![0]!["has_audio"]!.GetValue<bool>());
        Assert.AreEqual("vid-2", response["task_id"]!.GetValue<string>());
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Root, "out", "video_001.mp4")));
        Assert.IsGreaterThanOrEqualTo(2, polls);
    }

    [TestMethod]
    public async Task DownloadRejectsPendingWithoutWritingFile()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-3");
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(Success("{\"status\":\"pending\"}"))));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["download", "--request-id", "req-download", "--task-id", "vid-3", "--task-context-file", "context.json", "--api-key", "secret",
             "--api-url", "https://example.test/v1", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(3, code);
        Assert.AreEqual("task_not_ready", response["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }

    [TestMethod]
    public async Task TryRunMapsRequestAndIgnoresAudioWithoutNetwork()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("try-run must not access the provider")));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["generate", "--request-id", "req-try", "--prompt", "水墨山水", "--api-key", "secret",
             "--enable-audio", "--try-run"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
        Assert.AreEqual("grok-imagine-video-1.5", response["request_preview"]!["model"]!.GetValue<string>());
        Assert.AreEqual(8, response["request_preview"]!["duration"]!.GetValue<int>());
        Assert.AreEqual("480p", response["request_preview"]!["resolution"]!.GetValue<string>());
        Assert.AreEqual("/parameters/enable_audio", response["ignored_parameters"]![0]!["parameter"]!.GetValue<string>());
        Assert.DoesNotContain(output.ToString(), "secret");
    }

    [TestMethod]
    public async Task QueryStopsOnExpiredInsteadOfPolling()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-expired");
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            calls++;
            return Task.FromResult(Success("{\"status\":\"expired\"}"));
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["query", "--request-id", "req-expired", "--task-id", "vid-expired", "--task-context-file", "context.json", "--api-key", "secret",
             "--api-url", "https://example.test/v1", "--wait", "--timeout", "2"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(125, code);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task DownloadKeepsOriginalSlotWhenFileNameConflicts()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-slot");
        var outputDirectory = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "video_001.mp4"), "taken");
        using var client = new HttpClient(new FakeHandler(message =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/videos/vid-slot", StringComparison.Ordinal))
            {
                return Task.FromResult(Success("{\"status\":\"done\",\"model\":\"grok-imagine-video-1.5\",\"video\":{\"url\":\"https://cdn.example.test/video.mp4\",\"duration\":6}}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp4) });
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["download", "--request-id", "req-slot", "--task-id", "vid-slot", "--task-context-file", "context.json", "--api-key", "secret",
             "--api-url", "https://example.test/v1", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual(1, response["artifacts"]![0]!["index"]!.GetValue<int>());
        Assert.AreEqual("video_002.mp4", response["artifacts"]![0]!["path"]!.GetValue<string>());
        Assert.AreEqual("taken", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "video_001.mp4")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "video_002.mp4")));
    }

    [TestMethod]
    public async Task RequestFileOutputDirResolvesBesideTheFile()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "nested");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), """
            {
              "schema_version": "1.0",
              "request_id": "req-file-dir",
              "prompt": "river",
              "execution": { "output_dir": "../clips" }
            }
            """);
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("try-run must not access the provider")));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["generate", "--request-file", "nested/job.json", "--api-key", "secret", "--try-run"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(0, code);
        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
        Assert.AreEqual("../clips", response["effective_parameters"]!["execution"]!["output_dir"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task QueryRequiresContextBeforeAccessingProvider()
    {
        using var workspace = new TestWorkspace();
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("缺失上下文不能联网。")));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["query", "--request-id", "req-nomodel", "--task-id", "vid-nomodel", "--api-key", "secret",
             "--api-url", "https://example.test/v1"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(1, code);
        Assert.AreEqual("invalid_parameter", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("vid-nomodel", response["task_id"]!.GetValue<string>());
        Assert.IsNull(response["task_context"]);
    }

    [TestMethod]
    public async Task QueryRejectsContextModelThatDisagreesWithTheRemoteJob()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "context.json"), """
            {
              "schema_version": "1.0",
              "skill_id": "skill-grok-video",
              "provider": "grok",
              "task_id": "vid-mismatch",
              "task": "text-to-video",
              "model": "grok-imagine-video-1.5",
              "output_indices": [1],
              "provider_context": {}
            }
            """);
        using var client = new HttpClient(new FakeHandler(_ => Task.FromResult(Success("{\"status\":\"pending\",\"model\":\"grok-imagine-video\"}"))));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["query", "--request-id", "req-mismatch", "--task-id", "vid-mismatch", "--task-context-file", "context.json",
             "--api-key", "secret", "--api-url", "https://example.test/v1"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(125, code);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("grok-imagine-video-1.5", response["task_context"]!["model"]!.GetValue<string>());
        Assert.AreEqual("pending", response["provider_metadata"]!["remote_status"]!.GetValue<string>());
    }

    private static Task WriteContextAsync(TestWorkspace workspace, string taskId) =>
        File.WriteAllTextAsync(Path.Combine(workspace.Root, "context.json"), new JsonObject
        {
            ["schema_version"] = "1.0", ["skill_id"] = "skill-grok-video", ["provider"] = "grok",
            ["task_id"] = taskId, ["task"] = "text-to-video", ["model"] = "grok-imagine-video-1.5",
            ["output_indices"] = new JsonArray(1), ["provider_context"] = new JsonObject()
        }.ToJsonString());

    private static HttpResponseMessage Success(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
