namespace Madorin.AI.Runtime.MConsole.Hosting;

internal sealed class BoundedLineBuffer(int capacity)
{
    private readonly Queue<string> _lines = new(capacity);
    private readonly object _sync = new();

    public void Add(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_sync)
        {
            while (_lines.Count >= capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }
    }

    public string[] Snapshot()
    {
        lock (_sync)
        {
            return _lines.ToArray();
        }
    }
}
