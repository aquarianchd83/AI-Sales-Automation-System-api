using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>See <see cref="ICurrentUserPricingService"/>.</summary>
public class CurrentUserPricingService : ICurrentUserPricingService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentUserService _currentUser;

    public CurrentUserPricingService(UserManager<ApplicationUser> userManager, ICurrentUserService currentUser)
    {
        _userManager = userManager;
        _currentUser = currentUser;
    }

    public async Task<RegionalPricing> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is not { } userId)
            return RegionalPricingCatalog.UsdDefault;

        // IgnoreQueryFilters(): ApplicationUser carries a tenant filter, and this has to work for a
        // PlatformSuperAdmin, who belongs to no tenant.
        var countryCode = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);

        // Never null: an unset or unlisted country falls back to USD, same as every other price here.
        return RegionalPricingCatalog.Resolve(countryCode);
    }
}
