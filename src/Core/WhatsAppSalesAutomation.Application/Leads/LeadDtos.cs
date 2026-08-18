namespace WhatsAppSalesAutomation.Application.Leads;

public record LeadDto(
    Guid Id,
    Guid CustomerId,
    string CustomerPhoneNumberE164,
    string CustomerName,
    Guid? CampaignId,
    string Stage,
    string Score,
    int ScoreNumeric,
    string? Budget,
    string? Interest,
    string? PurchaseTimeline,
    Guid? AssignedTo,
    DateTime? LastActivityAt,
    DateTime CreatedAt);

public record LeadActivityDto(
    Guid Id,
    string ActivityType,
    string? OldValue,
    string? NewValue,
    string? Note,
    Guid? CreatedBy,
    DateTime CreatedAt);

/// <summary>A manual agent correction. All fields optional - only supplied ones change; Stage is the
/// only field that can also change automatically (AI-driven rescoring never touches Stage itself,
/// only Score/ScoreNumeric - see LeadService.RecomputeScore's doc comment for why).</summary>
public record UpdateLeadRequest(string? Stage, string? Budget, string? Interest, string? PurchaseTimeline);

public record AssignLeadRequest(Guid AgentId);

public record AddLeadActivityRequest(string Note);
