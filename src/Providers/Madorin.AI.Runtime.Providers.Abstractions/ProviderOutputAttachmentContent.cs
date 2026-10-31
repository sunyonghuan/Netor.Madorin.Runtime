using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>A confirmed local reference, never inline binary in a Provider event.</summary>
public sealed class ProviderOutputAttachmentContent(BlobReference blob, string? fileName) : AIContent
{
    public BlobReference Blob { get; } = blob;
    public string? FileName { get; } = fileName;
}
