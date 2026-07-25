namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Reads Provider HTTP content with a strict allocation limit.</summary>
public static class ProviderHttpContentReader
{
    public const int MaximumAllowedResponseBytes = 1_048_576;

    /// <summary>Reads the response body while enforcing the configured byte limit.</summary>
    public static async ValueTask<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumResponseBytes is <= 0 or > MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                maximumResponseBytes,
                $"Provider responses must be limited to between 1 byte and {MaximumAllowedResponseBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumResponseBytes, 16_384));
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumResponseBytes)
            {
                throw new InvalidDataException(
                    $"Provider response exceeded {maximumResponseBytes} bytes.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }
}
