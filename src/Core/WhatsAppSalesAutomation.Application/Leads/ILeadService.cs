using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Leads;

public interface ILeadService
{
    /// <summary><paramref name="stage"/>/<paramref name="score"/> filter to one LeadStage/LeadScoreBand
    /// value each when given (e.g. "Qualifying", "Hot").</summary>
    Task<PagedResult<LeadDto>> GetPagedAsync(PagedRequest request, string? stage = null, string? score = null, CancellationToken cancellationToken = default);

    Task<LeadDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A manual agent correction to Stage/Budget/Interest/PurchaseTimeline. Writes a
    /// LeadActivity per changed field with <paramref name="updatedByUserId"/> as CreatedBy.</summary>
    Task<LeadDto> UpdateAsync(Guid id, UpdateLeadRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default);

    Task<LeadDto> AssignAsync(Guid id, AssignLeadRequest request, CancellationToken cancellationToken = default);

    Task<LeadActivityDto> AddActivityAsync(Guid id, AddLeadActivityRequest request, Guid createdByUserId, CancellationToken cancellationToken = default);

    /// <summary>The timeline behind a lead's current Stage/Score/Budget/Interest/PurchaseTimeline -
    /// every automatic (CreatedBy null) and manual change, newest first, whether it came from an agent
    /// or from an AI turn.</summary>
    Task<PagedResult<LeadActivityDto>> GetActivitiesAsync(Guid id, PagedRequest request, CancellationToken cancellationToken = default);

    /// <summary>The customer's current non-terminal Lead (Stage not in Won/Lost), or a freshly created
    /// New-stage one if none exists - mirrors IConversationService.GetOrCreateActiveConversationIdAsync's
    /// "decide the active one in exactly one place" reasoning. Shared by CampaignSendService (a lead
    /// starting from a campaign) and ConversationOrchestrator (a lead starting from an inbound message
    /// with no campaign attribution, CampaignId left null).</summary>
    Task<Guid> GetOrCreateActiveLeadIdAsync(Guid customerId, Guid? campaignId, CancellationToken cancellationToken = default);
}
