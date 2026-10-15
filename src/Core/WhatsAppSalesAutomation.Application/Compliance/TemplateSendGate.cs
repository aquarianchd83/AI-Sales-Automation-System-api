using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Compliance;

/// <summary>The gate's answer. <see cref="Reason"/> is written for the person who sees it, not for a log.</summary>
public readonly record struct SendDecision(bool Allowed, string? Reason)
{
    public static SendDecision Allow { get; } = new(true, null);

    public static SendDecision Block(string reason) => new(false, reason);
}

/// <summary>
/// The one place that decides whether a WhatsApp template may go to a customer. Every code path that
/// sends a template to an end customer calls this immediately before it calls WhatsApp, so a new sender
/// cannot forget a rule and an old one cannot drift from the others.
///
/// A template is allowed only when all of these hold:
/// <list type="bullet">
/// <item>the customer has not opted out;</item>
/// <item>the customer is opted in, with a complete consent record - when, how, and the proof;</item>
/// <item>the consent scope covers the template: a Marketing-category template needs Marketing scope,
/// so booking-only consent never covers a promotion.</item>
/// </list>
/// Pure and synchronous on purpose: it reads the customer row it is handed and nothing else, so there is
/// nothing to mock and no way for it to be skipped by a failed lookup.
/// </summary>
public static class TemplateSendGate
{
    public static SendDecision Evaluate(Customer customer, TemplateCategory category)
    {
        if (customer.OptInStatus == OptInStatus.OptedOut)
            return SendDecision.Block("Customer has opted out.");

        if (customer.OptInStatus != OptInStatus.OptedIn)
            return SendDecision.Block("No consent on record - the customer has not opted in.");

        if (customer.OptInTimestamp is null
            || string.IsNullOrWhiteSpace(customer.OptInSource)
            || string.IsNullOrWhiteSpace(customer.OptInProof)
            || customer.OptInScope is null)
        {
            return SendDecision.Block("The consent record is incomplete (it needs when, how, proof and scope).");
        }

        if (category == TemplateCategory.Marketing && customer.OptInScope != ConsentScope.Marketing)
            return SendDecision.Block("The customer's consent does not cover promotional messages.");

        return SendDecision.Allow;
    }
}
