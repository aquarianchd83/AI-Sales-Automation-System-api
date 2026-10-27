using System.Security.Cryptography;
using System.Text;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Review;

/// <summary>
/// The fingerprint of what a script <i>is</i> for approval and for the video made from it: every scene's words, on-screen text and length, and
/// the checksum of the picture chosen for it - the picture's content, not just its name. Change one word, swap one picture for another, and
/// the fingerprint changes. Used to freeze what was approved and to prove, later, that a video or a post is of exactly that.
/// </summary>
public static class ContentSnapshot
{
    public static string Compute(IEnumerable<ContentScene> scenes, IReadOnlyDictionary<Guid, StudioAsset> assets)
    {
        var text = new StringBuilder();
        foreach (var scene in scenes.OrderBy(s => s.Order))
        {
            var checksum = scene.ChosenAssetId is { } id && assets.TryGetValue(id, out var asset) ? asset.Checksum : "none";
            text.Append(scene.Order).Append('\u001f').Append(scene.Narration).Append('\u001f').Append(scene.OnScreenText).Append('\u001f').Append(scene.DurationSeconds).Append('\u001f').Append(checksum).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}

public sealed record ReviewActionRequest(string? Comment);

public sealed record RejectRequest(string? Reason);

public sealed record ReviewEventDto(Guid Id, string Action, string? FromStatus, string ToStatus, string? ActorEmail, string? Comment, DateTime At);

/// <param name="CanApprove">Whether the person asking may approve or reject: an Admin always, a Sales Manager only if the tenant's approval policy allows it.</param>
/// <param name="IntegrityOk">For an approved script: it still has exactly the words and pictures that were approved. False means something changed behind the review's back and it must not be published.</param>
public sealed record ReviewStateDto(
    Guid VersionId, string Status, DateTime? SubmittedAt, string? SubmittedByEmail, DateTime? ReviewedAt, string? ReviewedByEmail, string? ReviewComment,
    bool CanApprove, bool ApprovalIsAdminOnly, bool IntegrityOk, IReadOnlyList<ReviewEventDto> History);

public interface IContentReviewService
{
    Task<ReviewStateDto> GetAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Sends a draft for approval. Every scene needs a picture and a verified fact behind what it says (checked again here), and a test-mode script cannot be sent.</summary>
    Task<ReviewStateDto> SubmitAsync(Guid tenantId, Guid versionId, ReviewActionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes a script back from review so it can be changed.</summary>
    Task<ReviewStateDto> WithdrawAsync(Guid tenantId, Guid versionId, ReviewActionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Approves it - only a person allowed to. The claims are checked once more against the facts as they are now, and what was approved is frozen.</summary>
    Task<ReviewStateDto> ApproveAsync(Guid tenantId, Guid versionId, ReviewActionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends it back, with a reason that is kept.</summary>
    Task<ReviewStateDto> RejectAsync(Guid tenantId, Guid versionId, RejectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts a new draft from an approved or rejected script; the original is left exactly as it was. Returns the new version's id.</summary>
    Task<Guid> ReviseAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);
}
