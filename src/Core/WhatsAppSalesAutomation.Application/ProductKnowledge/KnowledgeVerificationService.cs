using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using ValidationFailure = FluentValidation.Results.ValidationFailure;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <param name="Confirmed">The tenant's explicit "I have checked this": required for a price, guarantee, certification or result, and for an answer with Low confidence or a figure no source states.</param>
public record ApproveAnswerRequest(bool Confirmed);

public record RejectAnswerRequest(string? Reason);

/// <param name="Summary">The answer in the tenant's own words. Also how the tenant supplies what the sources lacked.</param>
public record EditAnswerRequest(string Summary, string? Detail, bool Confirmed);

public record ReopenAnswerRequest(string? Note);

/// <param name="ChosenValue">One of the conflicting values, or the tenant's own ("Rs 1,299 from 1 April").</param>
public record ResolveConflictRequest(string ChosenValue, string? Note);

public record AnswerRevisionDto(Guid Id, string Kind, string? FromStatus, string? ToStatus, string? FromText, string? ToText, string? Note, string? ActorEmail, DateTime At);

public interface IKnowledgeVerificationService
{
    Task<KnowledgeQuestionDto> ApproveAsync(Guid tenantId, Guid answerId, ApproveAnswerRequest request, CancellationToken cancellationToken = default);

    Task<KnowledgeQuestionDto> RejectAsync(Guid tenantId, Guid answerId, RejectAnswerRequest request, CancellationToken cancellationToken = default);

    /// <summary>The tenant's own answer: a correction to a draft, or the information the sources lacked. A new version - what it replaces stays in the history.</summary>
    Task<KnowledgeQuestionDto> EditAsync(Guid tenantId, Guid questionId, EditAnswerRequest request, CancellationToken cancellationToken = default);

    Task<KnowledgeQuestionDto> MarkUnknownAsync(Guid tenantId, Guid questionId, CancellationToken cancellationToken = default);

    Task<KnowledgeQuestionDto> MarkNotApplicableAsync(Guid tenantId, Guid questionId, CancellationToken cancellationToken = default);

    /// <summary>Takes an approved answer back for correction. The approval stays in the history.</summary>
    Task<KnowledgeQuestionDto> ReopenAsync(Guid tenantId, Guid answerId, ReopenAnswerRequest request, CancellationToken cancellationToken = default);

    Task<KnowledgeQuestionDto> ResolveConflictAsync(Guid tenantId, Guid conflictId, ResolveConflictRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnswerRevisionDto>> HistoryAsync(Guid tenantId, Guid questionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The tenant's decisions about the drafted answers, each one recorded: approve, edit, reject, "I don't know", "not applicable", reopen, and
/// choosing between values the sources disagree about. Approval means the tenant accepted the information - it does not prove an outside claim
/// is true - and it is never silent: a price or guarantee needs an explicit confirmation, test data cannot be approved, an answer whose
/// sources still disagree cannot be approved until the tenant has chosen a value, and an approval is never overwritten - an edit is a new
/// version and what it replaced stays in the history, with who did it and when.
/// </summary>
public sealed class KnowledgeVerificationService : IKnowledgeVerificationService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _user;
    private readonly ISourceLimitsProvider _limits;

    public KnowledgeVerificationService(IApplicationDbContext db, ICurrentUserService user, ISourceLimitsProvider limits)
    {
        _db = db;
        _user = user;
        _limits = limits;
    }

    // ----- approving and rejecting ---------------------------------------------------------------------------------------------------

    public async Task<KnowledgeQuestionDto> ApproveAsync(Guid tenantId, Guid answerId, ApproveAnswerRequest request, CancellationToken ct = default)
    {
        var (answer, question) = await FindCurrentAsync(tenantId, answerId, ct);

        if (answer.Status == KnowledgeVerificationStatus.VerifiedAndApproved)
        {
            // Approving an approved answer is only meaningful when it needs another look: a source changed, or an approved price has lapsed.
            var lapsed = answer.ReviewDueAt is { } due && due < DateTime.UtcNow;
            if (!answer.NeedsRevalidation && !lapsed)
                return await DtoAsync(tenantId, question, ct);

            var again = NeedsConfirmation(answer);
            if (again is not null && !request.Confirmed)
                throw Invalid("confirmed", $"{again} Tick \"I have checked this\" to confirm it is still correct.");

            var why2 = answer.NeedsRevalidation ? answer.RevalidationReason : "The approval of this price, guarantee or result had lapsed.";
            answer.NeedsRevalidation = false;
            answer.RevalidationReason = null;
            answer.ApprovedBy = _user.UserId;
            answer.ApprovedAt = DateTime.UtcNow;
            answer.ReviewDueAt = answer.IsMaterialClaim ? DateTime.UtcNow.AddDays(_limits.Get().MaterialReviewDays) : null;
            Record(tenantId, answer.QuestionId, answer.Id, KnowledgeRevisionKind.Approved, answer.Status, answer.Status, answer.Summary, answer.Summary, Trim($"Confirmed again. {why2}", 1000));
            await _db.SaveChangesAsync(ct);
            return await DtoAsync(tenantId, question, ct);
        }
        if (answer.IsSimulated)
            throw new ConflictException("This is test data from the simulator. It is not an answer and cannot be approved.");
        if (string.IsNullOrWhiteSpace(answer.Summary))
            throw new ConflictException("There is nothing to approve - the sources did not answer this. Write the answer yourself, or mark it unknown.");
        if (await _db.KnowledgeConflicts.AnyAsync(c => c.TenantId == tenantId && c.AnswerId == answer.Id && c.Status == KnowledgeConflictStatus.Open, ct))
            throw new ConflictException("Your sources disagree about a fact in this answer. Choose the right value first, then approve.");

        var why = NeedsConfirmation(answer);
        if (why is not null && !request.Confirmed)
            throw Invalid("confirmed", $"{why} Tick \"I have checked this\" to approve it.");

        var from = answer.Status;
        answer.Status = KnowledgeVerificationStatus.VerifiedAndApproved;
        answer.ApprovedBy = _user.UserId;
        answer.ApprovedAt = DateTime.UtcNow;
        answer.NeedsRevalidation = false;
        answer.RevalidationReason = null;
        answer.ReviewDueAt = answer.IsMaterialClaim ? DateTime.UtcNow.AddDays(_limits.Get().MaterialReviewDays) : null;
        Record(tenantId, answer.QuestionId, answer.Id, KnowledgeRevisionKind.Approved, from, answer.Status, null, answer.Summary, why is null ? null : "Confirmed after review: " + why);
        await _db.SaveChangesAsync(ct);
        return await DtoAsync(tenantId, question, ct);
    }

    public async Task<KnowledgeQuestionDto> RejectAsync(Guid tenantId, Guid answerId, RejectAnswerRequest request, CancellationToken ct = default)
    {
        var (answer, question) = await FindCurrentAsync(tenantId, answerId, ct);
        if (answer.Status == KnowledgeVerificationStatus.VerifiedAndApproved)
            throw new ConflictException("This answer is approved. Reopen it for correction before rejecting it.");

        var from = answer.Status;
        answer.Status = KnowledgeVerificationStatus.Rejected;
        Record(tenantId, answer.QuestionId, answer.Id, KnowledgeRevisionKind.Rejected, from, answer.Status, answer.Summary, null, Trim(request?.Reason, 1000));
        await _db.SaveChangesAsync(ct);
        return await DtoAsync(tenantId, question, ct);
    }

    public async Task<KnowledgeQuestionDto> ReopenAsync(Guid tenantId, Guid answerId, ReopenAnswerRequest request, CancellationToken ct = default)
    {
        var (answer, question) = await FindCurrentAsync(tenantId, answerId, ct);
        if (answer.Status != KnowledgeVerificationStatus.VerifiedAndApproved)
            throw new ConflictException("Only an approved answer needs reopening.");

        var from = answer.Status;
        answer.Status = KnowledgeVerificationStatus.CorrectionRequired;
        // The approval is not erased: who approved and when stays on the answer, and the history keeps the step.
        Record(tenantId, answer.QuestionId, answer.Id, KnowledgeRevisionKind.Reopened, from, answer.Status, answer.Summary, answer.Summary, Trim(request?.Note, 1000) ?? "Reopened for correction.");
        await _db.SaveChangesAsync(ct);
        return await DtoAsync(tenantId, question, ct);
    }

    // ----- the tenant's own words ----------------------------------------------------------------------------------------------------

    public async Task<KnowledgeQuestionDto> EditAsync(Guid tenantId, Guid questionId, EditAnswerRequest request, CancellationToken ct = default)
    {
        var question = await FindQuestionAsync(tenantId, questionId, ct);
        var summary = Regex.Replace(request?.Summary ?? string.Empty, @"[ \t]+", " ").Trim();
        var detail = string.IsNullOrWhiteSpace(request?.Detail) ? null : request!.Detail!.Trim();
        if (summary.Length < 3)
            throw Invalid("summary", "Write the answer in a few words at least.");
        if (summary.Length > 2000)
            throw Invalid("summary", "Keep the answer under 2,000 characters; put the rest in the details.");
        if (detail is { Length: > 8000 })
            throw Invalid("detail", "Keep the details under 8,000 characters.");

        var material = ClaimRules.IsMaterial($"{summary} {detail}");
        if (material && !request!.Confirmed)
            throw Invalid("confirmed", "This states a price, guarantee, certification or result, which customers will rely on. Tick \"I have checked this\" to save it.");

        var previous = await _db.KnowledgeAnswers.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.QuestionId == question.Id && a.IsCurrent, ct);
        var answer = Stub(tenantId, question, previous, KnowledgeVerificationStatus.VerifiedAndApproved);
        answer.Summary = summary;
        answer.Detail = detail;
        answer.IsMaterialClaim = material;
        answer.ReviewDueAt = material ? DateTime.UtcNow.AddDays(_limits.Get().MaterialReviewDays) : null;
        answer.ConfidenceReasons = "Written by you. It is your answer, not something found in the sources.";
        Supersede(previous, answer);

        var filledGap = string.IsNullOrWhiteSpace(previous?.Summary);
        Record(tenantId, question.Id, answer.Id, filledGap ? KnowledgeRevisionKind.InformationProvided : KnowledgeRevisionKind.Edited, previous?.Status, answer.Status,
            previous?.Summary, summary, material ? "Confirmed after review: states a price, guarantee, certification or result." : null);
        await _db.SaveChangesAsync(ct);
        return await DtoAsync(tenantId, question, ct);
    }

    public Task<KnowledgeQuestionDto> MarkUnknownAsync(Guid tenantId, Guid questionId, CancellationToken ct = default) =>
        MarkAsync(tenantId, questionId, KnowledgeVerificationStatus.InformationNotAvailable, KnowledgeRevisionKind.MarkedUnknown, "The tenant does not have this information.", ct);

    public Task<KnowledgeQuestionDto> MarkNotApplicableAsync(Guid tenantId, Guid questionId, CancellationToken ct = default) =>
        MarkAsync(tenantId, questionId, KnowledgeVerificationStatus.NotApplicable, KnowledgeRevisionKind.MarkedNotApplicable, "The tenant says this does not apply to their business.", ct);

    private async Task<KnowledgeQuestionDto> MarkAsync(Guid tenantId, Guid questionId, KnowledgeVerificationStatus status, KnowledgeRevisionKind kind, string note, CancellationToken ct)
    {
        var question = await FindQuestionAsync(tenantId, questionId, ct);
        var previous = await _db.KnowledgeAnswers.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.QuestionId == question.Id && a.IsCurrent, ct);
        if (previous?.Status == status)
            return await DtoAsync(tenantId, question, ct);

        // A new version with no answer text: an approved answer it replaces stays in the history, never overwritten.
        var answer = Stub(tenantId, question, previous, status);
        answer.ConfidenceReasons = note;
        Supersede(previous, answer);
        Record(tenantId, question.Id, answer.Id, kind, previous?.Status, status, previous?.Summary, null, note);
        await _db.SaveChangesAsync(ct);
        return await DtoAsync(tenantId, question, ct);
    }

    // ----- conflicts ---------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeQuestionDto> ResolveConflictAsync(Guid tenantId, Guid conflictId, ResolveConflictRequest request, CancellationToken ct = default)
    {
        var conflict = await _db.KnowledgeConflicts.FirstOrDefaultAsync(c => c.Id == conflictId && c.TenantId == tenantId, ct)
            ?? throw new NotFoundException("KnowledgeConflict", conflictId);
        var answer = conflict.AnswerId is { } id ? await _db.KnowledgeAnswers.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct) : null;
        if (answer is null)
            throw new NotFoundException("KnowledgeConflict", conflictId);
        if (conflict.Status == KnowledgeConflictStatus.Resolved)
            throw new ConflictException("This has already been decided. Reopen the answer if it needs to change.");

        var chosen = Regex.Replace(request?.ChosenValue ?? string.Empty, @"\s+", " ").Trim();
        if (chosen.Length == 0)
            throw Invalid("chosenValue", "Choose the right value, or write it.");
        if (chosen.Length > 500)
            throw Invalid("chosenValue", "Keep the value under 500 characters.");

        conflict.Status = KnowledgeConflictStatus.Resolved;
        conflict.ChosenValue = chosen;
        conflict.ResolvedBy = _user.UserId;
        conflict.ResolvedAt = DateTime.UtcNow;

        var question = await FindQuestionAsync(tenantId, answer.QuestionId, ct);
        _db.KnowledgeAnswerRevisions.Add(new KnowledgeAnswerRevision
        {
            TenantId = tenantId, QuestionId = question.Id, AnswerId = answer.Id, ConflictId = conflict.Id, Kind = KnowledgeRevisionKind.ConflictResolved,
            FromText = Trim($"Sources disagreed about the {conflict.Fact}.", 2000), ToText = Trim(chosen, 2000),
            Note = Trim(string.IsNullOrWhiteSpace(request?.Note) ? $"Chose \"{chosen}\" for the {conflict.Fact}." : $"Chose \"{chosen}\" for the {conflict.Fact}: {request!.Note!.Trim()}", 1000),
            ActorUserId = _user.UserId, ActorEmail = Trim(_user.Email, 256)
        });

        await _db.SaveChangesAsync(ct);
        var stillOpen = await _db.KnowledgeConflicts.AnyAsync(c => c.TenantId == tenantId && c.AnswerId == answer.Id && c.Status == KnowledgeConflictStatus.Open, ct);
        if (!stillOpen)
        {
            answer.HasConflict = false;
            await _db.SaveChangesAsync(ct);
        }

        return await DtoAsync(tenantId, question, ct);
    }

    // ----- the trail -------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AnswerRevisionDto>> HistoryAsync(Guid tenantId, Guid questionId, CancellationToken ct = default)
    {
        await FindQuestionAsync(tenantId, questionId, ct);
        var rows = await _db.KnowledgeAnswerRevisions.AsNoTracking().Where(r => r.TenantId == tenantId && r.QuestionId == questionId)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return rows.Select(r => new AnswerRevisionDto(r.Id, r.Kind.ToString(), r.FromStatus, r.ToStatus, r.FromText, r.ToText, r.Note, r.ActorEmail, r.CreatedAt)).ToList();
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Why a tenant has to say "I have checked this" before approving, or null when no extra confirmation is needed.</summary>
    internal static string? NeedsConfirmation(KnowledgeAnswer answer)
    {
        if (answer.IsMaterialClaim)
            return "This states a price, guarantee, certification or result, which customers will rely on.";
        if (answer.HasUnsupportedClaim)
            return "It contains a figure that none of your sources states.";
        if (answer.Confidence == KnowledgeConfidence.Low)
            return "The evidence for it is weak.";
        return null;
    }

    private static KnowledgeAnswer Stub(Guid tenantId, KnowledgeQuestion question, KnowledgeAnswer? previous, KnowledgeVerificationStatus status) => new()
    {
        TenantId = tenantId, QuestionId = question.Id, Version = (previous?.Version ?? 0) + 1, IsCurrent = true, GeneratedAt = DateTime.UtcNow, Provider = "Tenant",
        Kind = KnowledgeAnswerKind.TenantProvided, Confidence = KnowledgeConfidence.NoEvidence, Status = status,
        ApprovedAt = status == KnowledgeVerificationStatus.VerifiedAndApproved ? DateTime.UtcNow : null,
    };

    private void Supersede(KnowledgeAnswer? previous, KnowledgeAnswer answer)
    {
        if (previous is not null)
            previous.IsCurrent = false;
        answer.ApprovedBy = answer.Status == KnowledgeVerificationStatus.VerifiedAndApproved ? _user.UserId : answer.ApprovedBy;
        _db.KnowledgeAnswers.Add(answer);
    }

    private void Record(Guid tenantId, Guid questionId, Guid answerId, KnowledgeRevisionKind kind, KnowledgeVerificationStatus? from, KnowledgeVerificationStatus to, string? fromText, string? toText, string? note) =>
        _db.KnowledgeAnswerRevisions.Add(new KnowledgeAnswerRevision
        {
            TenantId = tenantId, QuestionId = questionId, AnswerId = answerId, Kind = kind, FromStatus = from?.ToString(), ToStatus = to.ToString(),
            FromText = Trim(fromText, 2000), ToText = Trim(toText, 2000), Note = Trim(note, 1000), ActorUserId = _user.UserId, ActorEmail = Trim(_user.Email, 256)
        });

    private async Task<(KnowledgeAnswer Answer, KnowledgeQuestion Question)> FindCurrentAsync(Guid tenantId, Guid answerId, CancellationToken ct)
    {
        var answer = await _db.KnowledgeAnswers.FirstOrDefaultAsync(a => a.Id == answerId && a.TenantId == tenantId, ct) ?? throw new NotFoundException("KnowledgeAnswer", answerId);
        if (!answer.IsCurrent)
            throw new ConflictException("A newer version of this answer exists. Reload to see it.");
        return (answer, await FindQuestionAsync(tenantId, answer.QuestionId, ct));
    }

    private async Task<KnowledgeQuestion> FindQuestionAsync(Guid tenantId, Guid questionId, CancellationToken ct)
    {
        return await _db.KnowledgeQuestions.FirstOrDefaultAsync(q => q.Id == questionId && q.TenantId == tenantId, ct) ?? throw new NotFoundException("KnowledgeQuestion", questionId);
    }

    private Task<KnowledgeQuestionDto> DtoAsync(Guid tenantId, KnowledgeQuestion question, CancellationToken ct) =>
        KnowledgeDraftingService.BuildQuestionDtoAsync(_db, tenantId, question, ct);

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        return v.Length <= max ? v : v[..max];
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
