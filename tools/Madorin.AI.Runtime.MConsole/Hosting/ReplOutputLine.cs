namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed record ReplOutputLine(DateTimeOffset Timestamp, bool IsError, string Text);
