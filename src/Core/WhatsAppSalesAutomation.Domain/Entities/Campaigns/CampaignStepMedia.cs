using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Entities.Media;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

/// <summary>
/// Join between a step and a media asset, with an explicit order - unlike Customer/CustomerTag's
/// implicit many-to-many, this needs its own row because <see cref="DisplayOrder"/> is data, not
/// just a key pair.
/// </summary>
public class CampaignStepMedia : BaseEntity
{
    public Guid CampaignStepId { get; set; }

    public Guid MediaAssetId { get; set; }

    public int DisplayOrder { get; set; }
}
