namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>Worker stdin/stdout frame limits and IPC version.</summary>
public static class ProviderWorkerIpc
{
    public const int Version = 2;

    public const int MaxBlobChunkBytes = 128 * 1024;
    public const int MaxBlobChunkFrameBytes = 256 * 1024;
    public const int MaxBufferedBlobBytes = 64 * 1024 * 1024;

    public const int MaxFrameLength = 16 * 1024 * 1024;

    public const string ErrorIpcVersionMismatch = "IpcVersionMismatch";

    public const string ErrorSlugMismatch = "SlugMismatch";

    public const string ErrorProtocolMismatch = "ProtocolMismatch";

    public const string ErrorInvokeFailed = "InvokeFailed";

    public const string ErrorWorkerCrashed = "WorkerCrashed";
}
