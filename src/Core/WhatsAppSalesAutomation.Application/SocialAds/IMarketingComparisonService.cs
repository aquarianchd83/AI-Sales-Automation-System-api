namespace WhatsAppSalesAutomation.Application.SocialAds;

public interface IMarketingComparisonService
{
    Task<MarketingComparisonDto> GetAsync(int months, CancellationToken cancellationToken = default);
}
