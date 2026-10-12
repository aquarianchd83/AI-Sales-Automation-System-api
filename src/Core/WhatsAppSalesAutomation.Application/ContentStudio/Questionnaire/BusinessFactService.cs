using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>See <see cref="IBusinessFactService"/>.</summary>
public class BusinessFactService : IBusinessFactService
{
    public const int MaxCategory = 60;
    public const int MaxStatement = 1000;

    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly IDateTimeProvider _dateTime;

    public BusinessFactService(IApplicationDbContext context, ITenantConfigOverrideProvider config, IDateTimeProvider dateTime)
    {
        _context = context;
        _config = config;
        _dateTime = dateTime;
    }

    public async Task<PagedResult<BusinessFactDto>> ListAsync(Guid tenantId, BusinessFactQuery query, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);

        var rows = _context.BusinessFacts.IgnoreQueryFilters().Where(f => f.TenantId == tenantId);
        if (query.Status is { } status) rows = rows.Where(f => f.Status == status);
        if (query.SourceType is { } source) rows = rows.Where(f => f.SourceType == source);
        if (!string.IsNullOrWhiteSpace(query.Category)) rows = rows.Where(f => f.Category == query.Category);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(f => f.Statement.Contains(term) || f.Category.Contains(term));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderBy(f => f.Status == BusinessFactStatus.Retired).ThenBy(f => f.Category).ThenBy(f => f.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var names = await NamesAsync(page.Where(f => f.VerifiedBy != null).Select(f => f.VerifiedBy!.Value).Distinct().ToList(), cancellationToken);
        return new PagedResult<BusinessFactDto>(page.Select(f => ToDto(f, names)).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<BusinessFactSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);

        var rows = await _context.BusinessFacts.IgnoreQueryFilters().Where(f => f.TenantId == tenantId)
            .Select(f => new { f.Status, f.Category }).ToListAsync(cancellationToken);

        return new BusinessFactSummaryDto(
            rows.Count(r => r.Status == BusinessFactStatus.Draft),
            rows.Count(r => r.Status == BusinessFactStatus.Verified),
            rows.Count(r => r.Status == BusinessFactStatus.Retired),
            rows.Where(r => r.Status != BusinessFactStatus.Retired).Select(r => r.Category).Distinct().OrderBy(c => c).ToList());
    }

    public async Task<BusinessFactDto> CreateAsync(Guid tenantId, Guid? actorUserId, SaveBusinessFactRequest request, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        Validate(request);

        var fact = new BusinessFact
        {
            TenantId = tenantId,
            Category = request.Category.Trim(),
            Statement = request.Statement.Trim(),
            SourceType = BusinessFactSource.Manual,
            Status = BusinessFactStatus.Draft,
            UpdatedBy = actorUserId
        };
        _context.BusinessFacts.Add(fact);
        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(fact, new Dictionary<Guid, string>());
    }

    public async Task<BusinessFactDto> UpdateAsync(Guid tenantId, Guid id, Guid? actorUserId, SaveBusinessFactRequest request, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        Validate(request);

        var fact = await FindAsync(tenantId, id, cancellationToken);
        if (fact.Status == BusinessFactStatus.Retired)
            throw new ConflictException("A retired fact cannot be changed. Add a new one instead.");
        if (fact.SourceType == BusinessFactSource.Questionnaire)
            throw new ConflictException("This fact comes from your questionnaire. Change your answer to that question instead.");

        var changed = fact.Statement != request.Statement.Trim() || fact.Category != request.Category.Trim();
        fact.Category = request.Category.Trim();
        fact.Statement = request.Statement.Trim();
        fact.UpdatedBy = actorUserId;

        if (changed && fact.Status == BusinessFactStatus.Verified)
        {
            // Whoever confirmed the old wording has not confirmed this one.
            fact.Status = BusinessFactStatus.Draft;
            fact.VerifiedBy = null;
            fact.VerifiedAtUtc = null;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(fact, await NamesAsync(fact.VerifiedBy is { } v ? new List<Guid> { v } : new List<Guid>(), cancellationToken));
    }

    public async Task<BusinessFactDto> VerifyAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);

        var fact = await FindAsync(tenantId, id, cancellationToken);
        if (fact.Status == BusinessFactStatus.Retired)
            throw new ConflictException("A retired fact cannot be verified.");

        if (fact.Status != BusinessFactStatus.Verified)
        {
            fact.Status = BusinessFactStatus.Verified;
            fact.VerifiedBy = actorUserId;
            fact.VerifiedAtUtc = _dateTime.UtcNow;
            fact.UpdatedBy = actorUserId;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ToDto(fact, await NamesAsync(new List<Guid> { fact.VerifiedBy ?? actorUserId }, cancellationToken));
    }

    public async Task<BusinessFactDto> RetireAsync(Guid tenantId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);

        var fact = await FindAsync(tenantId, id, cancellationToken);
        if (fact.Status != BusinessFactStatus.Retired)
        {
            fact.Status = BusinessFactStatus.Retired;
            fact.UpdatedBy = actorUserId;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ToDto(fact, new Dictionary<Guid, string>());
    }

    private async Task<BusinessFact> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        // Another tenant's id answers exactly as a missing one does.
        await _context.BusinessFacts.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, cancellationToken)
        ?? throw new NotFoundException("Fact not found.");

    private async Task<Dictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users.IgnoreQueryFilters().Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Email ?? string.Empty, cancellationToken);

    private static void Validate(SaveBusinessFactRequest request)
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(request.Category) || request.Category.Trim().Length > MaxCategory)
            failures.Add(new ValidationFailure(nameof(request.Category), $"Give the fact a category of up to {MaxCategory} characters."));
        if (string.IsNullOrWhiteSpace(request.Statement) || request.Statement.Trim().Length > MaxStatement)
            failures.Add(new ValidationFailure(nameof(request.Statement), $"Write the fact in up to {MaxStatement} characters."));
        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    private static BusinessFactDto ToDto(BusinessFact f, IReadOnlyDictionary<Guid, string> names) => new(
        f.Id, f.Category, f.Statement, f.SourceType, f.SourceRef, f.Status, f.VerifiedBy,
        f.VerifiedBy is { } v && names.TryGetValue(v, out var name) ? name : null, f.VerifiedAtUtc, f.CreatedAt, f.UpdatedAt);
}
