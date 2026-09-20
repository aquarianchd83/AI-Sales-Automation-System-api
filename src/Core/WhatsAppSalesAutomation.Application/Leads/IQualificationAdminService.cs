namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>
/// Manages the tenant's qualification schema and the values captured against it.
///
/// Separate from <see cref="ILeadService"/> on purpose: that service owns a lead's pipeline state
/// (stage, assignment, activity history), while this one owns what the business wants to find out and
/// what it has found out so far. They change for different reasons and at different times - a tenant
/// edits its schema once a quarter, and leads capture values every day.
/// </summary>
public interface IQualificationAdminService
{
    /// <summary>Every field this tenant has configured, active and inactive, in ask order. Not paged:
    /// the list is capped at <see cref="QualificationFieldLimits.MaxFieldsPerTenant"/>, so paging it
    /// would be ceremony over a list that always fits on one screen.</summary>
    Task<IReadOnlyList<QualificationFieldDto>> GetFieldsAsync(CancellationToken cancellationToken = default);

    Task<QualificationFieldDto> GetFieldAsync(Guid id, CancellationToken cancellationToken = default);

    Task<QualificationFieldDto> CreateFieldAsync(
        CreateQualificationFieldRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>FieldKey is not updatable - already-captured values are stored against it, so changing
    /// it would orphan them. Rename DisplayName instead.</summary>
    Task<QualificationFieldDto> UpdateFieldAsync(
        Guid id, UpdateQualificationFieldRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Flips IsActive. Deactivating takes the field out of the AI's schema immediately while
    /// leaving every captured value intact, which is what a tenant almost always means by "remove
    /// this question".</summary>
    Task<QualificationFieldDto> SetFieldActiveAsync(
        Guid id, bool isActive, Guid updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a field. Refused with a ConflictException when any lead has a value
    /// captured for it - that data is what customers actually told the business, and losing it to a
    /// misclick is not recoverable. Deactivate instead.</summary>
    Task DeleteFieldAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QualificationFieldDto>> ReorderFieldsAsync(
        ReorderQualificationFieldsRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Creates the default schema for a tenant that has none. Idempotent and additive: a
    /// tenant that already has fields gets nothing, and a tenant missing only some of the defaults
    /// gets only those. Called at signup and by the backfill.</summary>
    Task<IReadOnlyList<QualificationFieldDto>> SeedDefaultsAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>What is known and what is still missing for one lead.</summary>
    Task<LeadQualificationDto> GetLeadQualificationAsync(Guid leadId, CancellationToken cancellationToken = default);

    /// <summary>A human supplying or correcting a value. Stored with confidence 1.0 and the user's id,
    /// which also means the agent will never ask for it again.</summary>
    Task<LeadQualificationDto> SetLeadValueAsync(
        Guid leadId, string fieldKey, SetLeadQualificationValueRequest request, Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Clears a captured value so the agent asks for it again - the correction path for a
    /// value that was extracted wrongly and cannot simply be replaced with the right one.</summary>
    Task<LeadQualificationDto> ClearLeadValueAsync(
        Guid leadId, string fieldKey, CancellationToken cancellationToken = default);
}
