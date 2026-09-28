using System.Text;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// 一次响应流中的思考块。连续的思考增量合在同一块里；
/// 文本或工具调用把它截断，下一次思考另起一块。
/// </summary>
public sealed class ReasoningSignatureDraft
{
    private readonly bool _appendFragments;
    private readonly List<Block> _blocks = [];
    private Block? _current;

    /// <param name="appendFragments">
    /// true 时按到达顺序拼接签名片段（Anthropic signature_delta）；
    /// false 时后来的完整签名覆盖先前的值（OpenAI encrypted content）。
    /// </param>
    public ReasoningSignatureDraft(bool appendFragments) => _appendFragments = appendFragments;

    /// <summary>
    /// 记录一段思考或签名。返回需要推给调用方的思考文本；
    /// 纯签名增量返回 null，不进入对外事件。
    /// </summary>
    public string? Add(string? text, string? signature)
    {
        var hasText = !string.IsNullOrEmpty(text);
        var hasSignature = !string.IsNullOrEmpty(signature);
        if (!hasText && !hasSignature)
        {
            return null;
        }

        var created = false;
        if (_current is null)
        {
            _current = new Block();
            _blocks.Add(_current);
            created = true;
        }

        if (hasText)
        {
            _current.Text.Append(text);
        }

        if (hasSignature)
        {
            if (_appendFragments)
            {
                _current.Signature.Append(signature);
            }
            else
            {
                _current.Signature.Clear();
                _current.Signature.Append(signature);
            }
        }

        if (hasText)
        {
            return text;
        }

        return created ? string.Empty : null;
    }

    public void EndBlock() => _current = null;

    public void Commit(ReasoningSignatureCache cache, string providerId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        foreach (var block in _blocks)
        {
            if (block.Signature.Length == 0)
            {
                continue;
            }

            cache.Store(providerId, block.Text.ToString(), block.Signature.ToString());
        }
    }

    private sealed class Block
    {
        public StringBuilder Text { get; } = new();

        public StringBuilder Signature { get; } = new();
    }
}
