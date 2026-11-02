namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Physically deletes everything a tenant owns - its users, configuration, schedules and every tenant-scoped table -
/// and then the tenant row itself. Irreversible. Implemented against the EF model (every table carrying a <c>TenantId</c>), so
/// a table added later is covered without anyone remembering to list it here. The platform audit trail is deliberately kept.</summary>
public interface ITenantDataPurger
{
    Task<PurgeTenantResultDto> PurgeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
