using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Review;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

/// <summary>
/// The one rule that decides whether a video may go out: it must be the video of a script that is <i>approved</i>, that still has exactly the
/// words and pictures that were approved, made from exactly that content, and actually finished. The same check runs when publishing is asked
/// for and again just before the upload, so a script changed or a video replaced in between is caught - even if someone calls the API directly.
/// </summary>
public static class PublishGate
{
    /// <returns>A sentence saying what is wrong, or null when the video may be published.</returns>
    public static async Task<string?> ProblemAsync(IApplicationDbContext db, Guid tenantId, ContentVersion version, RenderJob? render, CancellationToken ct)
    {
        if (version.Status != ContentVersionStatus.Approved || string.IsNullOrEmpty(version.ApprovedHash))
            return "Only an approved script can be published. Send it for approval first.";

        var scenes = await db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && s.VersionId == version.Id).ToListAsync(ct);
        var ids = scenes.Where(s => s.ChosenAssetId != null).Select(s => s.ChosenAssetId!.Value).Distinct().ToList();
        var assets = (await db.StudioAssets.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id)).ToListAsync(ct)).ToDictionary(a => a.Id);
        if (ContentSnapshot.Compute(scenes, assets) != version.ApprovedHash)
            return "This script no longer matches what was approved - something was changed after the approval. It cannot be published. Start a new draft from it and send that for approval.";

        if (render is null || render.VersionId != version.Id || render.TenantId != tenantId)
            return "Choose a video made from this script.";
        if (render.Status != RenderJobStatus.Succeeded || render.IsSimulated || string.IsNullOrEmpty(render.OutputStorageKey))
            return "That video is not finished, or was only a test render, so there is nothing to publish.";
        if (render.ContentHash != version.ApprovedHash)
            return "That video was made before the script was approved, from different words or pictures. Make the video again from the approved script.";

        return null;
    }
}
