namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Bound from the "WhatsApp" config section.</summary>
public class WhatsAppSettings
{
    /// <summary>"Simulated" (default, no real credentials needed) or "Meta".</summary>
    public string Provider { get; set; } = "Simulated";

    public string PhoneNumberId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v19.0";

    /// <summary>0-100. Lets the retry pipeline be exercised without needing a real Meta failure.</summary>
    public int SimulatedFailureRatePercent { get; set; } = 0;
}
