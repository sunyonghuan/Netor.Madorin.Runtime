namespace Madorin.AI.Runtime.Cli.Config;

internal sealed class StandaloneConfigurationException : Exception
{
    public StandaloneConfigurationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public StandaloneConfigurationException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
