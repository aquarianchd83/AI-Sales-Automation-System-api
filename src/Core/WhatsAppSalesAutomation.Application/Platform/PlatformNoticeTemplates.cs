using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>What a send needs once the template for a notice has been found.</summary>
public record PlatformNoticeMessage(string TemplateName, string Language, IReadOnlyList<string> Parameters, string? MediaUrl);

/// <summary>Either the message to send or the reason there isn't one - recorded on the notification, never thrown.</summary>
public record PlatformNoticeResolution(PlatformNoticeMessage? Message, string? SkipNote = null);

/// <summary>Finds the platform's WhatsApp template for a kind of notice and fills it in for one tenant.</summary>
public interface IPlatformNoticeTemplates
{
    Task<PlatformNoticeResolution> ResolveAsync(TenantNotificationKind kind, string tenantName, string title, string message, CancellationToken cancellationToken = default);
}

public class PlatformNoticeTemplates : IPlatformNoticeTemplates
{
    private readonly IApplicationDbContext _context;
    private readonly IMediaStorageService? _storage;

    public PlatformNoticeTemplates(IApplicationDbContext context, IMediaStorageService? storage = null)
    {
        _context = context;
        _storage = storage;
    }

    public async Task<PlatformNoticeResolution> ResolveAsync(
        TenantNotificationKind kind, string tenantName, string title, string message, CancellationToken cancellationToken = default)
    {
        var key = kind.ToString();
        var template = await _context.PlatformMessageTemplates.FirstOrDefaultAsync(t => t.EventKey == key, cancellationToken);

        if (template is null)
            return new PlatformNoticeResolution(null, "whatsapp: this notice has no WhatsApp template");
        if (!template.IsActive)
            return new PlatformNoticeResolution(null, "whatsapp: this notice's template is switched off");

        // Meta only delivers an approved template; sending a pending one just earns an error, so say why it didn't go.
        if (template.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Approved)
            return new PlatformNoticeResolution(null, $"whatsapp: the template \"{template.WhatsAppTemplateName}\" is {template.WhatsAppTemplateStatus} on Meta, not Approved");

        var mediaUrl = await HeaderUrlAsync(template, cancellationToken);
        return new PlatformNoticeResolution(new PlatformNoticeMessage(
            template.WhatsAppTemplateName, template.Language, BuildParameters(template.BodyText, tenantName, title, message), mediaUrl));
    }

    /// <summary>The image link, only for a template Meta holds WITH an image header - Meta rejects a header parameter on one without.</summary>
    internal async Task<string?> HeaderUrlAsync(PlatformMessageTemplate template, CancellationToken cancellationToken)
    {
        if (!template.HeaderOnMeta || template.HeaderMediaAssetId is not { } id)
            return null;

        var asset = await _context.PlatformMediaAssets.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        return asset is null ? null : MediaAssetLinks.PublicUrl(asset.StorageProvider, asset.StorageKey, asset.Url, _storage);
    }

    /// <summary>One value per DISTINCT token in first-occurrence order - the positions Meta registered the template with.</summary>
    public static IReadOnlyList<string> BuildParameters(string bodyText, string tenantName, string title, string message)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PlatformTemplateCatalog.TenantNameToken] = PlatformTemplateCatalog.CleanValue(tenantName, "there"),
            [PlatformTemplateCatalog.TitleToken] = PlatformTemplateCatalog.CleanValue(title, "an update"),
            [PlatformTemplateCatalog.MessageToken] = PlatformTemplateCatalog.CleanValue(message, "Please open your dashboard for details.")
        };

        return TemplatePlaceholderResolver.ExtractTokens(bodyText)
            .Select(token => values.TryGetValue(token, out var v) ? v : "-")
            .ToList();
    }
}
