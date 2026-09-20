namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>
/// Manages the tenant's lead scoring rules, and reads back how a given lead's score was arrived at.
///
/// Rules are evaluated in C#, never by the model. A language model does not apply arithmetic
/// consistently, and <c>Lead.ScoreNumeric</c> is CRM state the sales team sorts and filters on - a
/// number that comes out differently on two runs over the same conversation is not one they can act
/// on. The model reports what happened; what it is worth is the business's decision, expressed here.
/// </summary>
public interface ILeadScoringAdminService
{
    /// <summary>Every rule this tenant has configured, in evaluation order. Not paged - the list is
    /// capped and always fits on one screen.</summary>
    Task<IReadOnlyList<LeadScoringRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default);

    Task<LeadScoringRuleDto> GetRuleAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LeadScoringRuleDto> CreateRuleAsync(
        CreateLeadScoringRuleRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default);

    Task<LeadScoringRuleDto> UpdateRuleAsync(
        Guid id, UpdateLeadScoringRuleRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Hard delete, unlike a qualification field. A rule holds no customer-supplied data, and
    /// the contributions it already produced keep their denormalized SourceKey/DisplayName, so past
    /// breakdowns stay readable after it is gone.</summary>
    Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates the default rules for a tenant that has none. Idempotent and additive, same
    /// contract as the qualification seed.</summary>
    Task<IReadOnlyList<LeadScoringRuleDto>> SeedDefaultsAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>What the admin UI needs to build a rule: the rule types with what each one's match
    /// value means, the intent names IntentMatch accepts, and this tenant's own field keys.</summary>
    Task<LeadScoringCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>A lead's current score with the contributions that produced it.</summary>
    Task<LeadScoreBreakdownDto> GetBreakdownAsync(Guid leadId, CancellationToken cancellationToken = default);
}
