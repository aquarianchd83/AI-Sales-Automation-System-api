namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What became of a payment. Every row from before a real gateway is <see cref="Succeeded"/>: a simulated payment could not fail.</summary>
public enum PaymentStatus
{
    Succeeded = 0,

    /// <summary>The gateway tried to take the money and could not. Recorded so the tenant can see it; it grants nothing and is not revenue.</summary>
    Failed = 1
}
