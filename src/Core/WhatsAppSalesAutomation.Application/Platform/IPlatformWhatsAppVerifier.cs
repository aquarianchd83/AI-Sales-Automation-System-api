namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Checks the SAVED platform WhatsApp credentials against Meta without sending any message.</summary>
public interface IPlatformWhatsAppVerifier
{
    /// <summary>Never throws: a missing setup, a rejected token or an unreachable Meta comes back as a failed result with the reason.</summary>
    Task<DeliveryTestResultDto> VerifyAsync(CancellationToken cancellationToken = default);
}
