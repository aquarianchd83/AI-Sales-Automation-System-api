using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>Trivial holder - see IEmbeddingProviderCatalog's own doc comment. The three concrete
/// clients are handed in by DependencyInjection.AddAiClients, resolved via their own concrete-type
/// registrations (separate from the single interface-bound IEmbeddingService registration that picks
/// the "active" one).</summary>
public class EmbeddingProviderCatalog : IEmbeddingProviderCatalog
{
    public IReadOnlyList<IEmbeddingService> AllProviders { get; }

    public EmbeddingProviderCatalog(IEnumerable<IEmbeddingService> providers)
    {
        AllProviders = providers.ToList();
    }
}
