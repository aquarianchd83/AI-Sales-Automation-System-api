using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Infrastructure.Identity;

/// <summary>
/// Scoped implementation of <see cref="ITenantContext"/>. Defaults to whatever
/// <see cref="ICurrentUserService"/> reads off the current request's JWT; <see cref="SetTenant"/>
/// overrides that for the request lifetime once set (used by the per-tenant background-job loop and
/// the WhatsApp webhook endpoint - see <see cref="ITenantContext"/>'s own doc comment).
/// </summary>
public class TenantContext : ITenantContext
{
    private readonly ICurrentUserService _currentUserService;
    private Guid? _overrideTenantId;
    private bool _hasOverride;
    private bool _platformScope;

    public TenantContext(ICurrentUserService currentUserService)
    {
        _currentUserService = currentUserService;
    }

    public Guid? TenantId => _hasOverride ? _overrideTenantId : _currentUserService.TenantId;

    public bool IsPlatformSuperAdmin => _platformScope || _currentUserService.Roles.Contains(AppRoles.PlatformSuperAdmin);

    public void SetTenant(Guid tenantId)
    {
        _overrideTenantId = tenantId;
        _hasOverride = true;
    }

    public void EnterPlatformScope()
    {
        // No tenant AND platform rights: the same shape a SuperAdmin request has, which is what the
        // scoped-or-global query filter and the stamping interceptor already know how to treat.
        _overrideTenantId = null;
        _hasOverride = true;
        _platformScope = true;
    }
}
