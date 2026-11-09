using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Reserves the same output allowance that is sent in the provider request.</summary>
public sealed record ContextTokenBudget(int InputLimit, int OutputLimit, int? ContextWindow)
{
    public static ContextTokenBudget Resolve(
        ProviderModel? model, int? explicitInputBudget = null, int? requestedMaxOutputTokens = null)
    {
        var window = model?.ContextWindow is > 0 ? model.ContextWindow : null;
        var outputLimit = requestedMaxOutputTokens is > 0
            ? requestedMaxOutputTokens.Value
            : Math.Min(model?.MaxOutputTokens is > 0 ? model.MaxOutputTokens.Value : 4096,
                window is { } known ? Math.Max(1, known / 10) : 4096);
        if (model?.MaxOutputTokens is > 0)
        {
            outputLimit = Math.Min(outputLimit, model.MaxOutputTokens.Value);
        }
        // 未声明窗口时不猜测模型限制，也不阻断调用；只应用显式预算，由 Provider 校验实际容量。
        var inputLimit = explicitInputBudget is > 0 ? explicitInputBudget.Value : int.MaxValue;
        if (window is { } contextWindow)
        {
            inputLimit = Math.Min(inputLimit, Math.Min((int)(contextWindow * 0.9), contextWindow - outputLimit));
        }
        if (inputLimit <= 0)
        {
            throw new InvalidOperationException("context_budget_exhausted: The output reserve leaves no room for input.");
        }
        return new ContextTokenBudget(inputLimit, outputLimit, window);
    }
}
