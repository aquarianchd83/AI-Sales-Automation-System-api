using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Setup;

/// <summary>
/// One answer, keyed by <see cref="FieldKey"/> rather than by requirement id. That is deliberate: a plan
/// change or a version migration swaps the requirement rows underneath an application, and an answer to
/// "brand_name" must follow the application across them. An answer whose key the current version no longer
/// asks is kept (switching back restores it) but ignored - never validated, never executed.
///
/// Stored in a canonical string form: text as is, numbers invariant, a checkbox "true"/"false", a date
/// yyyy-MM-dd, a multi-select a JSON array of option values.
/// </summary>
public class ApplicationSetupValue : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ApplicationId { get; set; }

    public string FieldKey { get; set; } = string.Empty;

    public string? FieldValue { get; set; }

    public Guid? UpdatedBy { get; set; }
}
