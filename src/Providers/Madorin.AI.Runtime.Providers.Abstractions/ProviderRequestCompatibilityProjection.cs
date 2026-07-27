namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Contains a Provider-safe request clone and its projection diagnostics.</summary>
public sealed record ProviderRequestCompatibilityProjection(
    RuntimeProviderRequest Request,
    ProviderRequestCompatibilityDiagnostics Diagnostics);
