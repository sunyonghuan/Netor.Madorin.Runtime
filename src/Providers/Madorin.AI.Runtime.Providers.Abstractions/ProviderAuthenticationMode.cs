namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Controls whether a Provider HTTP pipeline emits credentials.</summary>
public enum ProviderAuthenticationMode
{
    /// <summary>A non-empty credential is required and sent with each request.</summary>
    Required,

    /// <summary>No authentication header is sent.</summary>
    None
}
