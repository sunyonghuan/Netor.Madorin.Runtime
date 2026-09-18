using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

internal static class ProviderWorkerLog
{
    public static void Write(RuntimeDiagnosticLog log, string line)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        log.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Warning,
                "ProviderWorkerStderr",
                error: new RuntimeError(
                    "ProviderWorkerStderr",
                    "provider",
                    line,
                    false,
                    null,
                    Guid.NewGuid().ToString("N")))
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }
}
