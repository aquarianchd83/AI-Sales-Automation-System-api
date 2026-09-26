using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

/// <summary>
/// A business found by the lead discovery job that passed the tenant's qualification rules. Only
/// qualified, new leads are stored - a rejected or duplicate candidate leaves no row, so every row here is
/// implicitly "Qualified".
///
/// Each one is also added to the CRM as a <see cref="Entities.Customers.Customer"/> (<see cref="CustomerId"/>)
/// with WhatsApp consent set to OptedIn (OptInSource "Lead discovery"), in the same transaction as this row -
/// see LeadDiscoveryRunService. This row is kept alongside the customer as the discovery record - where the
/// business was found, what was verified, and how it scored.
///
/// Contact details are only kept when the pipeline could check them against a page it actually retrieved:
/// <see cref="Phone"/> must appear on a fetched page (<see cref="PhoneSourceUrl"/>), and so must
/// <see cref="Email"/>. A detail that could not be checked is dropped rather than kept as probably right.
/// </summary>
public class DiscoveredLead : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public string BusinessType { get; set; } = string.Empty;

    public string? ContactPerson { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>As published on <see cref="PhoneSourceUrl"/>.</summary>
    public string? Phone { get; set; }

    /// <summary><see cref="Phone"/> in E.164 form, or null when it could not be normalized. What a
    /// discovered business is matched against Customers.PhoneNumberE164 by.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>True whenever <see cref="Phone"/> is set - an unverified number is never stored.</summary>
    public bool PhoneVerified { get; set; }

    /// <summary>The fetched page the phone number was found on.</summary>
    public string? PhoneSourceUrl { get; set; }

    public string? Email { get; set; }

    public string? Website { get; set; }

    /// <summary>The page the business was discovered on.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>0-100 relevance to the tenant's target profile.</summary>
    public int LeadScore { get; set; }

    public string? ScoreRationale { get; set; }

    /// <summary>The CRM customer created for this business. Null when the lead had no phone number in E.164
    /// form, which Customer requires - the discovery row is still kept so the find is not lost. A plain Guid,
    /// not a foreign key, matching how every other cross-aggregate reference in this codebase is held.</summary>
    public Guid? CustomerId { get; set; }

    // ---- Duplicate-detection keys - see LeadQualification for how each is derived. ----

    /// <summary>The last (up to) ten digits of <see cref="Phone"/>, so the same number written with or
    /// without a country code or trunk prefix matches.</summary>
    public string? PhoneKey { get; set; }

    /// <summary>Lower-cased host (without "www.") and path of <see cref="Website"/>.</summary>
    public string? WebsiteKey { get; set; }

    /// <summary>Business name plus city, lower-cased with punctuation and spacing collapsed.</summary>
    public string NameKey { get; set; } = string.Empty;
}
