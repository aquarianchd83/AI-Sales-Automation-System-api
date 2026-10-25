using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// Decides whether a tenant may ask customers for WhatsApp consent at all: its WhatsApp Consent Page must be published, active and valid.
/// Asked before any paid customer discovery and again before any consent SMS, so a tenant is never charged for leads it cannot yet contact.
/// </summary>
public interface IConsentPageGate
{
    Task<ConsentPageReadiness> EvaluateAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts "WhatsApp Consent Page Setup Required" in the tenant's in-app notifications. Raised once per unresolved problem: the same problem
    /// does not notify again, a different one (or the same one returning after a fix) does. Never throws.
    /// </summary>
    Task NotifyAsync(Guid tenantId, ConsentPageReadiness readiness, CancellationToken cancellationToken = default);
}

/// <param name="State">One of <see cref="ConsentPageReadinessStates"/>.</param>
/// <param name="Missing">The field names to fix, e.g. "businessName".</param>
/// <param name="Live">The published snapshot customers would see; set only when <see cref="IsReady"/>.</param>
public record ConsentPageReadiness(bool IsReady, string State, IReadOnlyList<string> Missing, ConsentPageVersion? Live, string Reason)
{
    /// <summary>The platform, not the tenant, is missing something (the public address of the consent page). Nothing the tenant can fix.</summary>
    public bool PlatformProblem => State == ConsentPageReadinessStates.PlatformNotConfigured;
}

public static class ConsentPageReadinessStates
{
    public const string Ready = "Ready";
    public const string NotConfigured = "NotConfigured";
    public const string Incomplete = "Incomplete";
    public const string Unpublished = "Unpublished";
    public const string Inactive = "Inactive";
    public const string PlatformNotConfigured = "PlatformNotConfigured";
}
