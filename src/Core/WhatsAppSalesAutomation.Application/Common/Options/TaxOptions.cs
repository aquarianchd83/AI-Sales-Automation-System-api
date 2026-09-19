namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>The tax a country charges on top of a price.</summary>
public class TaxCountryRule
{
    /// <summary>What the tax is called on an invoice: "GST", "VAT".</summary>
    public string Name { get; set; } = "Tax";

    public decimal RatePercent { get; set; }

    /// <summary>True where the tax splits by the buyer's state (India's GST: CGST + SGST inside the platform's own state,
    /// IGST anywhere else).</summary>
    public bool SplitByState { get; set; }
}

/// <summary>
/// Bound from "Tax", edited on the Platform Admin Console's Configuration page. Tax is always added on top of the price. A
/// country with no rule here (or a rate of 0) is charged no tax.
/// </summary>
public class TaxOptions
{
    /// <summary>The state the platform is registered in (a code from <c>IndianStates</c>) - decides CGST + SGST versus IGST.</summary>
    public string SupplierStateCode { get; set; } = "MH";

    public Dictionary<string, TaxCountryRule> Countries { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IN"] = new() { Name = "GST", RatePercent = 18m, SplitByState = true }
    };
}

/// <summary>Bound from "Fx". The one exchange rate the platform sets by hand: it converts USD provider costs into rupees and
/// puts every payment's INR equivalent on the admin's reports. It never changes a tenant's price.</summary>
public class FxOptions
{
    public decimal InrPerUsd { get; set; } = 83m;
}
