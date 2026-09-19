namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>See <see cref="ICurrentUserPricingService"/>. The platform administrator is always based in India, so every
/// platform-wide figure is quoted in rupees whoever is signed in - it no longer follows the operator's profile country.</summary>
public class CurrentUserPricingService : ICurrentUserPricingService
{
    public Task<RegionalPricing> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RegionalPricingCatalog.Resolve("IN"));
}
