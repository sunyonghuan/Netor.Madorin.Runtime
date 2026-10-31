using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Cli;

namespace Madorin.AI.Multimedia.Tests;

public sealed partial class GrokVideoApplicationTests
{
    [TestMethod]
    public async Task CancellationAfterPendingKeepsTaskContextAndLastObservedStatus()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-cancel");
        using var control = new DeferredControlStream("req-cancel");
        var polls = 0;
        using var client = new HttpClient(new CancellationHandler(async (_, token) =>
        {
            if (++polls == 1) { return Success("{\"status\":\"pending\"}"); }
            control.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("取消后不能继续查询。");
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["query", "--request-id", "req-cancel", "--task-id", "vid-cancel", "--task-context-file", "context.json",
                "--api-key", "placeholder", "--wait", "--timeout", "5"], output, workspace.Root, control);
        var response = JsonNode.Parse(output.ToString())!;

        Assert.AreEqual(130, code);
        Assert.AreEqual("cancelled", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("req-cancel", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("vid-cancel", response["task_id"]!.GetValue<string>());
        Assert.AreEqual("pending", response["provider_metadata"]!["remote_status"]!.GetValue<string>());
        Assert.AreEqual("unsupported", response["provider_metadata"]!["remote_cancellation"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
        Assert.AreEqual(2, polls);
    }

    [TestMethod]
    public async Task InvalidDownloadedVideoLeavesNoArtifactAndDoesNotSendApiKeyToCdn()
    {
        using var workspace = new TestWorkspace();
        await WriteContextAsync(workspace, "vid-invalid-file");
        var cdnAuthorization = "unset";
        using var client = new HttpClient(new FakeHandler(message =>
        {
            if (message.RequestUri!.Host == "api.example.test")
            {
                return Task.FromResult(Success("{\"status\":\"done\",\"model\":\"grok-imagine-video-1.5\",\"video\":{\"url\":\"https://cdn.example.test/video.mp4\"}}"));
            }

            cdnAuthorization = message.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("not an mp4"u8.ToArray()) });
        }));
        using var output = new StringWriter();

        var code = await new GrokVideoApplication(client, TimeSpan.Zero).RunAsync(
            ["download", "--request-id", "req-invalid-file", "--task-id", "vid-invalid-file", "--task-context-file", "context.json",
                "--api-key", "placeholder", "--api-url", "https://api.example.test/v1", "--output-dir", "out"],
            output, workspace.Root);
        var response = JsonNode.Parse(output.ToString())!;
        var outputDirectory = Path.Combine(workspace.Root, "out");

        Assert.AreEqual(125, code);
        Assert.AreEqual("provider_error", response["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("vid-invalid-file", response["task_id"]!.GetValue<string>());
        Assert.AreEqual("unsupported", response["provider_metadata"]!["remote_cancellation"]!.GetValue<string>());
        Assert.IsNotNull(response["task_context"]);
        Assert.IsNull(cdnAuthorization);
        Assert.IsTrue(Directory.Exists(outputDirectory));
        Assert.IsEmpty(Directory.GetFiles(outputDirectory));
    }

    private sealed class DeferredControlStream(string requestId) : Stream
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _frame;
        private bool _read;

        public void Release()
        {
            _frame = Encoding.UTF8.GetBytes("{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"" + requestId + "\"}\n");
            _ready.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) { return 0; }
            await _ready.Task.WaitAsync(cancellationToken);
            _read = true;
            var frame = _frame!;
            frame.AsMemory().CopyTo(buffer);
            return frame.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
