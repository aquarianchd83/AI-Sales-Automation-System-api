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
    public static async Task<string?> ResolveUrlAsync(IApplicationDbContext context, MessageTemplate template, CancellationToken cancellationToken)
    {
        if (!template.HeaderOnMeta || template.HeaderMediaAssetId is not { } assetId)
            return null;

        return await context.MediaAssets
            .Where(a => a.Id == assetId)
            .Select(a => a.Url)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
