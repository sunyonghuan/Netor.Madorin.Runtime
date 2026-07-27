using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public interface IHostEventProjector
{
    public ValueTask ProjectAsync(
        RuntimeEventEnvelope envelope,
        CancellationToken cancellationToken = default);
}
