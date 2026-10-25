namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// A demo-only record of the SMS messages the SIMULATED provider "sent", so a person can click the consent links without a phone. Kept in
/// memory only (lost on restart), per tenant and bounded. Filled only when ConsentPage:DemoOutboxEnabled is on, because the messages hold
/// working consent links; a real provider never writes here.
/// </summary>
public interface ISimulatedSmsOutbox
{
    void Add(Guid tenantId, string toPhoneE164, string body, DateTime sentAtUtc);

    /// <summary>The tenant's messages, newest first.</summary>
    IReadOnlyList<SimulatedSms> List(Guid tenantId);
}

public record SimulatedSms(DateTime SentAtUtc, string ToPhoneE164, string Body);
