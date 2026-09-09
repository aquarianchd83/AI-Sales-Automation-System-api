using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform Admin Console's Usage & Quotas screen (spec item #4).</summary>
public interface IPlatformUsageService
{
    Task<PagedResult<PlatformTenantUsageDto>> GetPagedAsync(PagedRequest query, CancellationToken cancellationToken = default);
}
