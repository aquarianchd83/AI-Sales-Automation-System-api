namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform Admin Console's WhatsApp Connections screen (spec item #5).</summary>
public interface IPlatformWhatsAppConnectionService
{
    /// <summary>Every tenant that has ever saved a WhatsApp config, newest-updated first - a small
    /// enough list in practice (one row per tenant that has connected a WABA) that this isn't paged.</summary>
    Task<IReadOnlyList<PlatformWhatsAppConnectionDto>> GetAllAsync(CancellationToken cancellationToken = default);
}
