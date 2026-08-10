namespace WhatsAppSalesAutomation.Domain.Common;

/// <summary>
/// Marks an entity as soft-deletable. Implementers are expected to register an EF Core
/// global query filter (see <c>CustomerConfiguration</c>) so deleted rows never surface
/// through normal queries.
/// </summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }

    DateTime? DeletedAt { get; set; }
}
