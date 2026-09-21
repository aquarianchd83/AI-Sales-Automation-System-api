namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// The platform's own embedding provider - independent of whichever tenant is in scope. See
/// <c>PlatformEmbeddingService</c> for why platform-authored knowledge needs one, and what it costs.
///
/// A distinct type rather than a second registration of <see cref="IEmbeddingService"/>, so a consumer
/// asks for it by name and cannot receive it by accident where it wanted the tenant's.
/// </summary>
public interface IPlatformEmbeddingService : IEmbeddingService
{
    /// <summary>True when platform knowledge is embedded by the Simulated stand-in - it will not be
    /// found by tenants using a real provider.</summary>
    bool IsSimulated { get; }

    /// <summary>A real provider is configured as the platform's embedder but its API key is missing, so
    /// it is silently running as Simulated. Almost always a mistake, unlike choosing Simulated on
    /// purpose.</summary>
    bool RealProviderRequestedButUnconfigured { get; }
}
