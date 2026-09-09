namespace WhatsAppSalesAutomation.Domain.Common;

/// <summary>
/// Marks an entity as belonging to exactly one tenant. Implementers get an EF Core global
/// query filter applied automatically (see <c>ApplicationDbContext.OnModelCreating</c>,
/// which reflects over every <see cref="ITenantOwned"/> type in the model rather than each
/// entity's own configuration calling <c>HasQueryFilter</c> individually - EF Core only
/// allows one filter lambda per entity, so <see cref="ISoftDelete"/> and
/// <see cref="ITenantOwned"/> filters for the same entity are combined there, not here).
/// <see cref="Infrastructure.Persistence.Interceptors.TenantStampingSaveChangesInterceptor"/>
/// stamps <see cref="TenantId"/> automatically on insert and guards against it changing on
/// update.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
