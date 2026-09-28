using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// 进程内的推理签名表。键是供应商连接和思考文本，值是要原样回传的签名。
/// 某条签名只要还出现在后续请求里就会续期；超过保留时间且没有再出现时删除。
/// </summary>
public sealed class ReasoningSignatureCache
{
    /// <summary>没有被后续请求续期时，签名在进程内保留的时间。</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly long _retentionTicks;

    public ReasoningSignatureCache(TimeProvider? time = null, TimeSpan? retention = null)
    {
        _time = time ?? TimeProvider.System;
        var window = retention ?? DefaultRetention;
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        }

        _retentionTicks = window.Ticks;
    }

    /// <summary>把本轮历史里仍然存在的思考块续期。没有对应签名时不新建条目。</summary>
    public void Touch(string providerId, IReadOnlyList<RuntimeProviderMessage>? messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (messages is null)
        {
            return;
        }

        var seenAt = _time.GetUtcNow().UtcTicks;
        foreach (var message in messages)
        {
            if (message?.Content is null)
            {
                continue;
            }

            foreach (var block in message.Content)
            {
                if (block is ReasoningContentBlock reasoning)
                {
                    TouchKey(CreateKey(providerId, reasoning.Content), seenAt);
                }
            }
        }
    }

    public string? TryGet(string providerId, string? reasoningText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _entries.TryGetValue(CreateKey(providerId, reasoningText), out var entry)
            ? entry.Signature
            : null;
    }

    public void Store(string providerId, string? reasoningText, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrEmpty(signature);
        _entries[CreateKey(providerId, reasoningText)] = new Entry(signature, _time.GetUtcNow().UtcTicks);
    }

    /// <summary>删除保留时间已过、且没有被续期的签名。</summary>
    public void Sweep()
    {
        var cutoff = _time.GetUtcNow().UtcTicks - _retentionTicks;
        foreach (var pair in _entries)
        {
            if (pair.Value.LastSeenTicks < cutoff)
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private void TouchKey(string key, long seenAt)
    {
        while (_entries.TryGetValue(key, out var entry))
        {
            if (_entries.TryUpdate(key, entry with { LastSeenTicks = seenAt }, entry))
            {
                return;
            }
        }
    }

    private static string CreateKey(string providerId, string? reasoningText)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(reasoningText ?? string.Empty));
        return string.Concat(providerId, "\u001f", Convert.ToHexString(hash));
    }

    private readonly record struct Entry(string Signature, long LastSeenTicks);
}
