using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// One picture the tenant may put in a video: something they uploaded, a screenshot they took, or a stock photo they chose. Every asset
/// carries where it came from and under what terms it may be used - an asset with no stated terms does not exist - and its kind is always
/// shown to the tenant, so a stock photo is never passed off as their own. The file itself is kept privately in the tenant's folder and is
/// read back only through the API.
/// </summary>
public class StudioAsset : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public StudioAssetKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Comma-separated words that help the picture be found for a scene.</summary>
    public string? Tags { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Where the private copy is kept (the tenant's own folder).</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of the file: the same picture is stored once per tenant.</summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>Who supplied a stock photo ("Pexels"); null for the tenant's own.</summary>
    public string? Provider { get; set; }

    public string? ProviderAssetId { get; set; }

    /// <summary>The page the photo was found on.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The terms it may be used under, e.g. "Provided by you" or "Pexels License".</summary>
    public string LicenseName { get; set; } = string.Empty;

    public string? LicenseUrl { get; set; }

    /// <summary>The credit line to show where the licence asks for one ("Photo by Jane Doe on Pexels").</summary>
    public string? Attribution { get; set; }

    /// <summary>The tenant confirmed they may use the picture and that anyone identifiable in it has agreed.</summary>
    public bool RightsConfirmed { get; set; }

    /// <summary>For a screenshot: the tenant confirmed it shows no patient or customer personal data.</summary>
    public bool PersonalDataChecked { get; set; }

    public Guid? CreatedBy { get; set; }
}
