namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Bound from "ServiceCost". What a service costs the platform in rupees, typed in by the platform admin on the Service margins screen, for the
/// services whose cost the platform does not record itself (Content Studio). Null means none was typed, so there is no margin to show.</summary>
public class ManualServiceCostOptions
{
    public decimal? ContentStudioGenerationInr { get; set; }

    public decimal? ContentStudioVideoRenderInr { get; set; }
}
