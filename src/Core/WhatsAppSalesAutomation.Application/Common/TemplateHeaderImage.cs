using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>Finds the image a template message must carry as its WhatsApp header. Meta requires the header
/// parameter on EVERY send of a template it holds with an image header, and rejects one for a template without it -
/// so this returns a link only when the template was created there with the image (<c>HeaderOnMeta</c>), whichever
/// path (campaign or agent) is sending.</summary>
public static class TemplateHeaderImage
{
    /// <param name="storage">When given, the link is rebuilt from the current MediaStorage configuration, so a
    /// PublicBaseUrl set after the file was uploaded still applies; otherwise the URL stored at upload is used.</param>
    public static async Task<string?> ResolveUrlAsync(IApplicationDbContext context, MessageTemplate template, CancellationToken cancellationToken, IMediaStorageService? storage = null)
    {
        if (!template.HeaderOnMeta || template.HeaderMediaAssetId is not { } assetId)
            return null;

        var asset = await context.MediaAssets
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Url, a.StorageKey })
            .FirstOrDefaultAsync(cancellationToken);

        if (asset is null)
            return null;

        return storage is null ? asset.Url : storage.GetPublicUrl(asset.StorageKey);
    }
}
