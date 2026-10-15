namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// How Meta rates the quality of a tenant's WhatsApp number, which falls when customers block or report it.
/// It drives what the tenant may send: Green sends normally and earns higher caps over time, Yellow halves the
/// cap and warns the tenant, Red pauses sending until a person has reviewed the tenant.
/// </summary>
public enum WhatsAppQualityRating
{
    Green = 0,
    Yellow = 1,
    Red = 2
}
