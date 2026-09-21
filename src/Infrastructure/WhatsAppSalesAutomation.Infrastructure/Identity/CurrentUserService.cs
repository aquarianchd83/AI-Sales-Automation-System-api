using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Identity;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string? Email => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Email);

    public IReadOnlyList<string> Roles =>
        _httpContextAccessor.HttpContext?.User?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        ?? new List<string>();

    public Guid? TenantId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User?.FindFirstValue(JwtClaimNames.TenantId);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public Guid? ImpersonatorUserId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User?.FindFirstValue(JwtClaimNames.ImpersonatedBy);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string? IpAddress
    {
        get
        {
            var remote = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
            if (remote is null)
                return null;

            // An IPv4 client on a dual-stack socket arrives as ::ffff:a.b.c.d; record the plain form.
            return remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString();
        }
    }
}
