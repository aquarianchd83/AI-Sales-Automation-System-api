using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Handoffs;

public class HandoffService : IHandoffService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<ResolveHandoffRequest> _resolveValidator;

    public HandoffService(IApplicationDbContext context, IDateTimeProvider dateTime, IValidator<ResolveHandoffRequest> resolveValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _resolveValidator = resolveValidator;
    }

    public async Task<PagedResult<HandoffDto>> GetPagedAsync(PagedRequest request, string? status = null, CancellationToken cancellationToken = default)
    {
        HandoffStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<HandoffStatus>(status, ignoreCase: true, out var parsed))
                throw Invalid("status", $"Status must be one of: {string.Join(", ", Enum.GetNames<HandoffStatus>())}.");
            parsedStatus = parsed;
        }

        var filtered = _context.HumanHandoffs.Where(h => parsedStatus == null || h.Status == parsedStatus);

        var totalCount = await filtered.CountAsync(cancellationToken);

        // Ordering and paging happen entirely on the raw HumanHandoffs queryable - plain column
        // access, fully translatable. Only the Id survives into pagedIds; see BaseQuery's comment
        // for why NOTHING (not Where, not OrderBy, not Skip/Take) may be chained after a Select
        // into the HandoffRow record, which is why the join/shape happens in a second query below
        // instead of being fused into this one.
        var pagedIds = await filtered
            .OrderBy(h => h.Status) // Pending first (0), then Assigned/InProgress, Resolved last
            .ThenBy(h => h.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken);

        var rows = await BaseQuery(h => pagedIds.Contains(h.Id)).ToListAsync(cancellationToken);

        // The second query has no ORDER BY of its own, so the page order from pagedIds is
        // reapplied client-side (rows.length == pagedIds.length always, since pagedIds came
        // straight from HumanHandoffs and every handoff has exactly one conversation/customer).
        var ordered = pagedIds.Select(id => ToDto(rows.First(r => r.Handoff.Id == id))).ToList();

        return new PagedResult<HandoffDto>(ordered, totalCount, request.Page, request.PageSize);
    }

    public async Task<HandoffDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await BaseQuery(h => h.Id == id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(HumanHandoff), id);

        return ToDto(row);
    }

    public async Task<HandoffDto> ClaimAsync(Guid id, Guid agentId, CancellationToken cancellationToken = default)
    {
        var handoff = await FindOrThrowAsync(id, cancellationToken);

        if (handoff.Status == HandoffStatus.Resolved)
            throw new ConflictException("This handoff is already Resolved and cannot be claimed.");

        handoff.Status = HandoffStatus.Assigned;
        handoff.AssignedAgentId = agentId;
        handoff.AssignedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<HandoffDto> ResolveAsync(Guid id, ResolveHandoffRequest request, CancellationToken cancellationToken = default)
    {
        await _resolveValidator.ValidateAndThrowAsync(request, cancellationToken);

        var handoff = await FindOrThrowAsync(id, cancellationToken);

        handoff.Status = HandoffStatus.Resolved;
        handoff.ResolvedAt = _dateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Notes))
            handoff.Notes = request.Notes.Trim();

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<HandoffDto> GetOrCreateOpenHandoffAsync(Guid conversationId, string triggerReason, string? notes, CancellationToken cancellationToken = default)
    {
        var existingId = await _context.HumanHandoffs
            .Where(h => h.ConversationId == conversationId && h.Status != HandoffStatus.Resolved)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingId.HasValue)
            return await GetByIdAsync(existingId.Value, cancellationToken);

        var handoff = new HumanHandoff
        {
            ConversationId = conversationId,
            TriggerReason = Enum.Parse<HandoffTriggerReason>(triggerReason, ignoreCase: true),
            Notes = notes
        };
        _context.HumanHandoffs.Add(handoff);
        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(handoff.Id, cancellationToken);
    }

    // Two things here are deliberate, both learned from an actual untranslatable-query failure:
    //
    // 1. The `filter` predicate is applied to the raw HumanHandoffs queryable BEFORE the
    //    join/select, never as a .Where() (or any other operator - OrderBy/Skip/Take fail exactly
    //    the same way) layered on top of the finished BaseQuery() result. EF Core cannot translate
    //    ANY further query operator that reaches into a custom record type (HandoffRow)
    //    constructed by an earlier .Select() - it doesn't matter whether that record holds plain
    //    entity references or computed values, the projection itself blocks further server-side
    //    composition. The Select into HandoffRow must therefore be the LAST thing this queryable
    //    does before ToListAsync/FirstOrDefaultAsync; callers that need filtering, ordering, or
    //    paging (GetPagedAsync) do it on the raw entities first and pass the result in as `filter`.
    //
    // 2. The select projects the Customer ENTITY itself, not Customer.FullName - FullName is a
    //    C#-side computed expression (string.Join over FirstName/LastName), and referencing a
    //    computed property inside any IQueryable projection makes the whole query untranslatable
    //    for the same reason. FullName is evaluated later in ToDto, after materialization -
    //    mirroring ConversationService's ToDto pattern.
    private IQueryable<HandoffRow> BaseQuery(Expression<Func<HumanHandoff, bool>> filter) =>
        from h in _context.HumanHandoffs.Where(filter)
        join conv in _context.Conversations on h.ConversationId equals conv.Id
        join cust in _context.Customers on conv.CustomerId equals cust.Id
        select new HandoffRow(h, cust);

    private async Task<HumanHandoff> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.HumanHandoffs.FirstOrDefaultAsync(h => h.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(HumanHandoff), id);

    private static HandoffDto ToDto(HandoffRow row) => new(
        row.Handoff.Id,
        row.Handoff.ConversationId,
        row.Customer.Id,
        row.Customer.PhoneNumberE164,
        row.Customer.FullName,
        row.Handoff.TriggerReason.ToString(),
        row.Handoff.Status.ToString(),
        row.Handoff.AssignedAgentId,
        row.Handoff.AssignedAt,
        row.Handoff.ResolvedAt,
        row.Handoff.Notes,
        row.Handoff.CreatedAt);

    private record HandoffRow(HumanHandoff Handoff, Customer Customer);

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
