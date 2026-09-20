using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

public class Customer : BaseEntity, ISoftDelete, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string PhoneNumberE164 { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Email { get; set; }

    /// <summary>Free-text origin, e.g. "Import", "Manual", "Website".</summary>
    public string? Source { get; set; }

    public OptInStatus OptInStatus { get; set; } = OptInStatus.PendingOptIn;

    public DateTime? OptInTimestamp { get; set; }

    /// <summary>
    /// How consent was captured, e.g. "WebForm", "WhatsApp", "PaperForm", "Import". Together with
    /// <see cref="OptInTimestamp"/> this is the evidence that the customer agreed to be messaged -
    /// the status alone proves nothing after the fact.
    /// </summary>
    public string? OptInSource { get; set; }

    public DateTime? OptOutTimestamp { get; set; }

    /// <summary>How the opt-out was detected. Null when the customer has never opted out. Compliance
    /// questions ("did they really ask us to stop?") need the answer, and an AiDetected one is the
    /// only non-deterministic path, so it is the one worth spot-checking.</summary>
    public OptOutSource? OptOutSource { get; set; }

    public string? PreferredLanguage { get; set; }

    public Guid? AssignedAgentId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public ICollection<CustomerTag> Tags { get; set; } = new List<CustomerTag>();

    public string FullName =>
        string.Join(" ", new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
