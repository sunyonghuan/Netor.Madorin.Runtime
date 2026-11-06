namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>Identifies the semantic role of text written to an interactive CLI terminal.</summary>
public enum CliTextStyle
{
    Default,
    Title,
    Command,
    Parameter,
    Selected,
    Prompt,
    Success,
    Warning,
    Error,
    Muted,

    /// <summary>欢迎界面里的马头轮廓。</summary>
    Horse,

    /// <summary>马头上需要先被认出来的强调点。</summary>
    Accent,

    /// <summary>用户正在输入和已发送的对话。</summary>
    User,

    /// <summary>AI 回复的对话正文。</summary>
    Assistant
}
