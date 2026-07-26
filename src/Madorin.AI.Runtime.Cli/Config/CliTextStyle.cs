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
    Muted
}
