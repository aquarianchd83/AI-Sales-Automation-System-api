using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

public class Customer : BaseEntity, ISoftDelete
{
    public string PhoneNumberE164 { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Email { get; set; }

    /// <summary>Free-text origin, e.g. "Import", "Manual", "Website".</summary>
    public string? Source { get; set; }

    public OptInStatus OptInStatus { get; set; } = OptInStatus.PendingOptIn;

    public DateTime? OptInTimestamp { get; set; }

    public DateTime? OptOutTimestamp { get; set; }

    public string? PreferredLanguage { get; set; }

    public Guid? AssignedAgentId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public ICollection<CustomerTag> Tags { get; set; } = new List<CustomerTag>();

    public string FullName =>
        string.Join(" ", new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
