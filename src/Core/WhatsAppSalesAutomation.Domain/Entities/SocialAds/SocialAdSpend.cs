using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.SocialAds;

/// <summary>
/// One day of social media ad results for one platform: what was spent and what it got. Rows come from the
/// Meta sync (<see cref="Source"/> = "Meta", one per ad account, day and platform - "facebook" or
/// "instagram") or from the tenant typing in a month's spend (<see cref="Source"/> = "Manual", dated the 1st
/// of the month, platform "manual") when it has no ad account to connect.
/// </summary>
public class SocialAdSpend : BaseEntity, ITenantOwned
{
    public const string SourceMeta = "Meta";
    public const string SourceManual = "Manual";
    public const string ManualPlatform = "manual";

    public Guid TenantId { get; set; }

    public string Source { get; set; } = SourceMeta;

    /// <summary>The ad account the row came from; empty for a manual entry (kept non-null so the unique key works).</summary>
    public string AdAccountId { get; set; } = string.Empty;

    /// <summary>The calendar day in the ad account's own time zone, as Meta reports it (time of day unused).</summary>
    public DateTime Date { get; set; }

    public string Platform { get; set; } = string.Empty;

    public decimal Spend { get; set; }

    public long Impressions { get; set; }

    public long Clicks { get; set; }

    /// <summary>Leads Meta attributes to the ads (lead forms and pixel leads).</summary>
    public int Leads { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;
}
