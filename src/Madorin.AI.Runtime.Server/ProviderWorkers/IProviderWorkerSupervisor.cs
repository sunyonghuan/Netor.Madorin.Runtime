using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public interface IProviderWorkerSupervisor : IAsyncDisposable
{
    public IRuntimeProviderAdapter CreateAdapter(string protocol, ProviderConnectionConfig connection);
}
