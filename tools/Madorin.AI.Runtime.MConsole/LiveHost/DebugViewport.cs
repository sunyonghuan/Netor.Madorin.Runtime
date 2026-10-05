namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>一轮运行时，输出区和事件区在控制台中的位置。</summary>
public sealed class DebugViewport
{
    public int Origin { get; set; } = -1;

    public int OutputRows { get; set; }

    public int EventRows { get; set; }
}
