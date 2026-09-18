namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>Raised when a serialized Worker frame exceeds <see cref="ProviderWorkerIpc.MaxFrameLength"/>.</summary>
public sealed class ProviderWorkerFrameTooLargeException : InvalidOperationException
{
    public ProviderWorkerFrameTooLargeException(int length)
        : base($"Serialized frame length {length} exceeds {ProviderWorkerIpc.MaxFrameLength}.")
    {
        Length = length;
    }

    public int Length { get; }
}
