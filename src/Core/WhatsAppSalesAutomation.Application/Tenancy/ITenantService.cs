namespace WhatsAppSalesAutomation.Application.Tenancy;

public interface ITenantService
{
    /// <summary>Public, pre-login lookup used by the frontend's branded login page - throws
    /// <see cref="Common.Exceptions.NotFoundException"/> for an unknown slug, never leaks anything
    /// beyond <see cref="TenantPublicDto"/>'s cosmetic fields.</summary>
    Task<TenantPublicDto> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
}
