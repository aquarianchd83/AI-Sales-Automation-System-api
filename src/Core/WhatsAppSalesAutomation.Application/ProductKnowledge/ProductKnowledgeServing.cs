namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>
/// Whether customer chat reads a tenant's published product knowledge (ON) or the knowledge base articles as it always has (OFF, the default).
/// It is a platform setting made per tenant by the platform administrator (<c>ProductKnowledge:ServeChat</c> as a tenant override over the platform
/// default); a tenant sees the state but does not change it. Kept apart from <c>ITenantConfigOverrideProvider</c> so the chat's existing options
/// plumbing is untouched.
/// </summary>
public interface IProductKnowledgeServing
{
    /// <summary>For the ambient tenant of the current request or job.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>For a named tenant (the tenant's own screens and background work that carry a tenant id).</summary>
    Task<bool> IsEnabledForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
