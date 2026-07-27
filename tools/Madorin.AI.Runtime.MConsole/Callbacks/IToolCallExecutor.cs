using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public interface IToolCallExecutor
{
    public ValueTask<ToolCallResponse> ExecuteAsync(
        ToolCallRequest request,
        CancellationToken cancellationToken = default);
}
