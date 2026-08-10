namespace WhatsAppSalesAutomation.Domain.Common;

/// <summary>
/// Base type for entities that own their own primary key and audit timestamps.
/// <see cref="Infrastructure.Persistence.Interceptors.AuditableEntitySaveChangesInterceptor"/>
/// stamps <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> automatically on save.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
