namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>How a captured qualification value is validated and normalized. Deliberately small: every
/// type here has one unambiguous normalization, and a type whose normalization would be a guess would
/// put guessed data into the CRM. Stored as a string (see QualificationFieldConfiguration) so
/// reordering or extending this never renumbers existing rows - same convention as QuotaType.</summary>
public enum QualificationDataType
{
    Text = 0,

    Number = 1,

    /// <summary>"1 cr" / "50k" / "₹12,000" - normalized to a plain decimal string in the tenant's own
    /// currency. The currency itself is not captured: a tenant's customers quote amounts in the
    /// tenant's currency, and asking the model to infer one would invent precision that isn't there.</summary>
    Currency = 2,

    /// <summary>Normalized to ISO-8601 date (no time). Relative phrasings the customer actually uses
    /// ("next month", "after Diwali") normalize to null rather than to a guessed date - the raw value
    /// is kept, and a human reading the lead gets the customer's own words.</summary>
    Date = 3,

    Boolean = 4,

    /// <summary>Requires AllowedValuesJson. A captured value outside the list is rejected, not stored -
    /// an unconstrained value in a constrained field silently breaks every filter and report built on
    /// that field.</summary>
    SingleChoice = 5,

    /// <summary>Requires AllowedValuesJson. Normalized to a semicolon-joined subset of the allowed
    /// values, in the order they are declared, so two leads that picked the same options compare equal.</summary>
    MultiChoice = 6,

    PhoneNumber = 7,

    Email = 8
}
