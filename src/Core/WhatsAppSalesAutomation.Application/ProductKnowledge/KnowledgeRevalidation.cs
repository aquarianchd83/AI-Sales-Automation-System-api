using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>
/// Finds the answers a changed source puts in doubt. An answer cites the passage it was drawn from by that passage's fingerprint; when a
/// source is read again, an answer whose passage is no longer there as written - changed, or gone - is marked "needs another look", and so is
/// every answer drawn from a source that was removed. Nothing else is touched: an answer whose passages are unchanged is left alone, an
/// approval is not withdrawn (the tenant decides), and what is already published keeps serving until they publish again. The flag, the
/// reason and the step in the audit trail are the only things written.
/// </summary>
public static class KnowledgeRevalidation
{
    public static async Task<int> MarkAffectedAsync(IApplicationDbContext db, Guid tenantId, Guid sourceId, bool sourceRemoved, CancellationToken ct)
    {
        var evidence = await db.AnswerEvidences.AsNoTracking().Where(e => e.TenantId == tenantId && e.SourceId == sourceId).ToListAsync(ct);
        if (evidence.Count == 0)
            return 0;

        var source = sourceRemoved ? null : await db.KnowledgeSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId, ct);
        var gone = sourceRemoved || source is null;
        var hashes = gone
            ? new HashSet<string>()
            : (await db.SourcePassages.AsNoTracking().Where(p => p.TenantId == tenantId && p.SourceId == sourceId && p.SourceVersion == source!.Version).Select(p => p.Hash).ToListAsync(ct)).ToHashSet();

        var answerIds = evidence.Select(e => e.AnswerId).Distinct().ToList();
        var answers = await db.KnowledgeAnswers.Where(a => a.TenantId == tenantId && a.IsCurrent && answerIds.Contains(a.Id) && !a.NeedsRevalidation).ToListAsync(ct);

        var flagged = 0;
        foreach (var answer in answers)
        {
            var stale = evidence.Where(e => e.AnswerId == answer.Id && (gone || !hashes.Contains(e.PassageHash))).ToList();
            if (stale.Count == 0)
                continue;

            var name = stale[0].SourceName;
            var reason = gone
                ? $"{name} was removed."
                : $"{name} changed: the words this answer was based on are no longer there as written.";
            answer.NeedsRevalidation = true;
            answer.RevalidationReason = reason.Length > 500 ? reason[..500] : reason;
            db.KnowledgeAnswerRevisions.Add(new KnowledgeAnswerRevision
            {
                TenantId = tenantId, QuestionId = answer.QuestionId, AnswerId = answer.Id, Kind = KnowledgeRevisionKind.SourceChanged,
                FromStatus = answer.Status.ToString(), ToStatus = answer.Status.ToString(), Note = answer.RevalidationReason
            });
            flagged++;
        }

        if (flagged > 0)
            await db.SaveChangesAsync(ct);
        return flagged;
    }
}
