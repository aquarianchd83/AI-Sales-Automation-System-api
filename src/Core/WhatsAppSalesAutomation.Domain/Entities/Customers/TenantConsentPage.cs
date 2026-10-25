using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

/// <summary>
/// A tenant's own WhatsApp Consent Page content: what the customer sees about the business and the wording they agree to. One row per
/// tenant. This is the editable DRAFT; customers only ever see a published <see cref="ConsentPageVersion"/>, so editing here never
/// changes a page already live, nor the evidence behind a past decision.
/// </summary>
public class TenantConsentPage : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The business, product or service being offered.</summary>
    public string? BusinessName { get; set; }

    public string? ProductDescription { get; set; }

    /// <summary>Optional plain text.</summary>
    public string? AdditionalInfo { get; set; }

    /// <summary>The wording the customer agrees to. May use [Business Name] and [Product/Service]; null/blank means the default.</summary>
    public string? ConsentStatement { get; set; }

    public string? PrivacyPolicyUrl { get; set; }

    public string? TermsUrl { get; set; }

    /// <summary>False takes the page down: nothing is published until the tenant publishes again.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The version customers currently see; 0 until the first publish.</summary>
    public int PublishedVersion { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>The draft was saved after the last publish.</summary>
    public bool HasUnpublishedChanges { get; set; }
}

/// <summary>
/// An immutable snapshot of the consent page at the moment it was published, with the consent statement already resolved to the exact
/// text the customer reads. A decision records this version, so what a customer agreed to can always be shown later. Never updated.
/// </summary>
public class ConsentPageVersion : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public int Version { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    /// <summary>What the offer is called in the SMS and the statement; null when the tenant named none.</summary>
    public string? ProductLabel { get; set; }

    public string ProductDescription { get; set; } = string.Empty;

    public string? AdditionalInfo { get; set; }

    /// <summary>The exact consent sentence shown next to the checkbox.</summary>
    public string ConsentStatement { get; set; } = string.Empty;

    public string? PrivacyPolicyUrl { get; set; }

    public string? TermsUrl { get; set; }

    public DateTime PublishedAtUtc { get; set; }
}
