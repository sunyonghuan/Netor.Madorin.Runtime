using System.Diagnostics;

namespace Madorin.AI.Runtime.Cli.Commands;

internal sealed class ConsoleReplInterruptSource : IReplInterruptSource
{
    private ConsoleReplInterruptSource()
    {
    }

    public static ConsoleReplInterruptSource Instance { get; } = new();

    public IDisposable Subscribe(Action interruptHandler)
    {
        ArgumentNullException.ThrowIfNull(interruptHandler);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            try
            {
                interruptHandler();
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "Console interrupt callback failure type={0} diagnosticId={1}",
                    exception.GetType().FullName,
                    $"diag_cancel_{Guid.NewGuid():N}");
            }
        };
        Console.CancelKeyPress += handler;
        return new Subscription(handler);
    }

    private sealed class Subscription(ConsoleCancelEventHandler handler) : IDisposable
    {
        private ConsoleCancelEventHandler? _handler = handler;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _handler, null);
            if (current is not null)
            {
                Console.CancelKeyPress -= current;
            }
        }
    }
}
