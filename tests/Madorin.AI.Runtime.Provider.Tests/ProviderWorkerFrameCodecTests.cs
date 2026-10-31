using System.Buffers.Binary;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderWorkerFrameCodecTests
{
    [TestMethod]
    public async Task WriteThenRead_HelloFrame_RoundTrips()
    {
        await using var stream = new MemoryStream();
        var hello = new HelloFrame(1, "OpenAI Compatible", "openai-compatible", "0.1.46");

        await ProviderWorkerFrameCodec.WriteAsync(stream, hello);
        stream.Position = 0;

        var read = await ProviderWorkerFrameCodec.ReadAsync(stream);
        Assert.AreEqual(ProviderWorkerFrameReadStatus.Frame, read.Status);
        var restored = read.Frame as HelloFrame;
        Assert.IsNotNull(restored);
        Assert.AreEqual(1, restored.IpcVersion);
        Assert.AreEqual("openai-compatible", restored.Slug);
        Assert.AreEqual("OpenAI Compatible", restored.Protocol);
    }

    [TestMethod]
    public async Task Write_LengthAboveMax_ThrowsFrameTooLargeWithoutWriting()
    {
        await using var stream = new MemoryStream();
        var payload = new string('a', ProviderWorkerIpc.MaxFrameLength + 1);
        var frame = new ErrorFrame(null, ProviderWorkerIpc.ErrorInvokeFailed, payload);
        await Assert.ThrowsExactlyAsync<ProviderWorkerFrameTooLargeException>(
            () => ProviderWorkerFrameCodec.WriteAsync(stream, frame));
        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    public async Task Read_LengthAboveMax_ReturnsOversizeWithoutConsumingBody()
    {
        await using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)ProviderWorkerIpc.MaxFrameLength + 1);
        await stream.WriteAsync(header);
        stream.WriteByte(0x7B);
        stream.Position = 0;

        var read = await ProviderWorkerFrameCodec.ReadAsync(stream);
        Assert.AreEqual(ProviderWorkerFrameReadStatus.Oversize, read.Status);
        Assert.IsNull(read.Frame);
        Assert.AreEqual(ProviderWorkerIpc.MaxFrameLength + 1, read.DeclaredLength);
        Assert.AreEqual(4, stream.Position);
    }

    [TestMethod]
    public async Task RunAsync_HelloIpcVersionMismatch_WritesHelloRejected()
    {
        await using var stdin = new MemoryStream();
        await using var stdout = new MemoryStream();
        await ProviderWorkerFrameCodec.WriteAsync(
            stdin,
            new HelloFrame(ProviderWorkerIpc.Version - 1, "OpenAI Compatible", "openai-compatible", "0.1.46"));
        stdin.Position = 0;

        var exit = await ProviderWorkerHost.RunAsync(
            "OpenAI Compatible",
            "openai-compatible",
            _ => throw new InvalidOperationException("adapter must not be built"),
            stdin,
            stdout,
            TextWriter.Null);

        Assert.AreEqual(1, exit);
        stdout.Position = 0;
        var read = await ProviderWorkerFrameCodec.ReadAsync(stdout);
        var rejected = read.Frame as HelloRejectedFrame;
        Assert.IsNotNull(rejected);
        Assert.AreEqual(ProviderWorkerIpc.ErrorIpcVersionMismatch, rejected.Code);
    }
}
