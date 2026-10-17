using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>
/// Writes a fact's history. Every place that changes a fact calls <see cref="RecordAsync"/> right after the change, so the revision
/// holds the fact as it now stands and the reason - one entry per change, numbered per fact, never edited afterwards.
/// </summary>
internal static class BusinessFactHistory
{
    public static async Task RecordAsync(
        IApplicationDbContext context, BusinessFact fact, BusinessFactChangeKind kind, string? note, Guid? changedBy, bool isNew, CancellationToken cancellationToken)
    {
        var number = 1;
        if (!isNew)
        {
            var last = await context.BusinessFactRevisions.IgnoreQueryFilters()
                .Where(r => r.TenantId == fact.TenantId && r.FactId == fact.Id)
                .MaxAsync(r => (int?)r.Number, cancellationToken);
            number = (last ?? 0) + 1;
        }

        context.BusinessFactRevisions.Add(new BusinessFactRevision
        {
            TenantId = fact.TenantId,
            FactId = fact.Id,
            Number = number,
            Kind = kind,
            Category = fact.Category,
            Subject = fact.Subject,
            Statement = fact.Statement,
            Context = fact.Context,
            Status = fact.Status,
            Note = note is { Length: > 300 } ? note[..300] : note,
            ChangedBy = changedBy
        });
    }
}
