using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Application.Leads;

public static class LeadMappings
{
    public static LeadDto ToDto(this Lead lead, Customer customer) => new(
        lead.Id,
        lead.CustomerId,
        customer.PhoneNumberE164,
        customer.FullName,
        lead.CampaignId,
        lead.Stage.ToString(),
        lead.Score.ToString(),
        lead.ScoreNumeric,
        lead.Budget,
        lead.Interest,
        lead.PurchaseTimeline,
        lead.AssignedTo,
        lead.LastActivityAt,
        lead.CreatedAt);

    public static LeadActivityDto ToDto(this LeadActivity activity) => new(
        activity.Id,
        activity.ActivityType.ToString(),
        activity.OldValue,
        activity.NewValue,
        activity.Note,
        activity.CreatedBy,
        activity.CreatedAt);
}
