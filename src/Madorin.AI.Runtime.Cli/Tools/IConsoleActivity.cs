namespace Madorin.AI.Runtime.Cli.Tools;

/// <summary>交互运行时占一行的等待状态。正式输出前要先结束这一行。</summary>
internal interface IConsoleActivity
{
    public void Start();

    public void Finish();
}
