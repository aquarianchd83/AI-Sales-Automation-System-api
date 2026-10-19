using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>See <see cref="IBusinessFactService"/>.</summary>
public class BusinessFactService : IBusinessFactService
{
    public const int MaxCategory = 60;
    public const int MaxStatement = 1000;
    public const int MaxSubject = 120;
    public const int MaxContext = 300;
    public const int MaxEvidence = 500;

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
        var now = _dateTime.UtcNow;

        var rows = _context.BusinessFacts.IgnoreQueryFilters().Where(f => f.TenantId == tenantId);
        if (query.Status is { } status) rows = rows.Where(f => f.Status == status);
        if (query.SourceType is { } source) rows = rows.Where(f => f.SourceType == source);
        if (!string.IsNullOrWhiteSpace(query.Category)) rows = rows.Where(f => f.Category == query.Category);
        if (query.Classification is { } classification)
        {
            // Classification is worked out from status, origin and dates, so it is filtered by the same facts it is computed from.
            rows = classification switch
            {
                KnowledgeClassification.Retired => rows.Where(f => f.Status == BusinessFactStatus.Retired),
                KnowledgeClassification.Expired => rows.Where(f => f.Status == BusinessFactStatus.Verified && f.ExpiresAtUtc != null && f.ExpiresAtUtc <= now),
                KnowledgeClassification.TenantApproved => rows.Where(f => f.Status == BusinessFactStatus.Verified && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now)),
                KnowledgeClassification.TenantProvided => rows.Where(f => f.Status == BusinessFactStatus.Draft && f.Origin == BusinessFactOrigin.TenantProvided),
                KnowledgeClassification.SystemDerived => rows.Where(f => f.Status == BusinessFactStatus.Draft && f.Origin == BusinessFactOrigin.SystemDerived),
                _ => rows.Where(f => f.Status == BusinessFactStatus.Draft && f.Origin == BusinessFactOrigin.AiInferred)
            };
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(f => f.Statement.Contains(term) || f.Category.Contains(term) || (f.Subject != null && f.Subject.Contains(term)));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderBy(f => f.Status == BusinessFactStatus.Retired).ThenBy(f => f.Category).ThenBy(f => f.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var names = await NamesAsync(page.Where(f => f.VerifiedBy != null).Select(f => f.VerifiedBy!.Value).Distinct().ToList(), cancellationToken);
        return new PagedResult<BusinessFactDto>(page.Select(f => ToDto(f, names, now)).ToList(), total, query.Page, query.PageSize);
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
            Subject = Clean(request.Subject),
            Context = Clean(request.Context),
            Evidence = Clean(request.Evidence),
            ExpiresAtUtc = request.ExpiresAtUtc,
            Statement = request.Statement.Trim(),
            SourceType = BusinessFactSource.Manual,
            Origin = BusinessFactOrigin.TenantProvided,
            Status = BusinessFactStatus.Draft,
            UpdatedBy = actorUserId
        };
        _context.BusinessFacts.Add(fact);
        await BusinessFactHistory.RecordAsync(_context, fact, BusinessFactChangeKind.Created, "Added by you.", actorUserId, true, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(fact, new Dictionary<Guid, string>(), _dateTime.UtcNow);
    }

    public async Task<BusinessFactDto> ProposeAsync(Guid tenantId, ProposeFactRequest request, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);

        if (request.Origin == BusinessFactOrigin.TenantProvided)
            throw new ArgumentException("A proposal comes from the system or an AI model; the tenant's own words are added as facts.", nameof(request));

        Validate(new SaveBusinessFactRequest(request.Category, request.Statement, request.Subject, request.Context));

        var fact = new BusinessFact
        {
            TenantId = tenantId,
            Category = request.Category.Trim(),
            Subject = Clean(request.Subject),
            Context = Clean(request.Context),
            Statement = request.Statement.Trim(),
            SourceType = BusinessFactSource.Manual,
            Origin = request.Origin,
            // However it was derived, it starts unconfirmed: nobody has stood behind it yet.
            Status = BusinessFactStatus.Draft
        };
        _context.BusinessFacts.Add(fact);
        await BusinessFactHistory.RecordAsync(
            _context, fact, BusinessFactChangeKind.Created, Truncate($"Proposed by the {(request.Origin == BusinessFactOrigin.AiInferred ? "AI" : "system")}. {request.Basis}".Trim(), 300), null, true, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(fact, new Dictionary<Guid, string>(), _dateTime.UtcNow);
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

        var changed = fact.Statement != request.Statement.Trim() || fact.Category != request.Category.Trim()
                      || fact.Subject != Clean(request.Subject) || fact.Context != Clean(request.Context);
        fact.Category = request.Category.Trim();
        fact.Statement = request.Statement.Trim();
        fact.Subject = Clean(request.Subject);
        fact.Context = Clean(request.Context);
        fact.Evidence = Clean(request.Evidence);
        fact.ExpiresAtUtc = request.ExpiresAtUtc;
        fact.UpdatedBy = actorUserId;

        var wasVerified = fact.Status == BusinessFactStatus.Verified;
        if (changed && wasVerified)
        {
            // Whoever confirmed the old wording has not confirmed this one.
            fact.Status = BusinessFactStatus.Draft;
            fact.VerifiedBy = null;
            fact.VerifiedAtUtc = null;
        }

        await BusinessFactHistory.RecordAsync(
            _context, fact, changed && wasVerified ? BusinessFactChangeKind.Invalidated : changed ? BusinessFactChangeKind.Edited : BusinessFactChangeKind.DetailsChanged,
            changed && wasVerified ? "You changed what it says, so it needs confirming again." : changed ? "You changed it." : "Details updated.", actorUserId, false, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(fact, await NamesAsync(fact.VerifiedBy is { } v ? new List<Guid> { v } : new List<Guid>(), cancellationToken), _dateTime.UtcNow);
    }

    public async Task<BusinessFactDto> UpdateDetailsAsync(Guid tenantId, Guid id, Guid? actorUserId, UpdateFactDetailsRequest request, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        ValidateEvidence(Clean(request.Evidence));
        if (request.ExpiresAtUtc is { } until && until <= _dateTime.UtcNow)
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.ExpiresAtUtc), "Choose an end date that is still to come.") });

        var fact = await FindAsync(tenantId, id, cancellationToken);
        if (fact.Status == BusinessFactStatus.Retired)
            throw new ConflictException("A retired fact cannot be changed.");

        // Proof and an end date describe the claim; they do not change what it says, so a verified fact stays verified.
        fact.Evidence = Clean(request.Evidence);
        fact.ExpiresAtUtc = request.ExpiresAtUtc;
        fact.UpdatedBy = actorUserId;

        await BusinessFactHistory.RecordAsync(_context, fact, BusinessFactChangeKind.DetailsChanged, "Proof or end date updated.", actorUserId, false, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(fact, await NamesAsync(fact.VerifiedBy is { } v ? new List<Guid> { v } : new List<Guid>(), cancellationToken), _dateTime.UtcNow);
    }

    public async Task<BusinessFactDto> VerifyAsync(Guid tenantId, Guid id, Guid actorUserId, bool reconfirm = false, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        var now = _dateTime.UtcNow;

        var fact = await FindAsync(tenantId, id, cancellationToken);
        if (fact.Status == BusinessFactStatus.Retired)
            throw new ConflictException("A retired fact cannot be verified.");
        if (fact.ReviewEveryDays is null && KnowledgeClassifier.IsExpired(fact, now))
            throw new ConflictException("The date this fact was valid until has passed. Set a new end date first, or retire it.");

        var wasVerified = fact.Status == BusinessFactStatus.Verified;
        if (wasVerified && !reconfirm && !KnowledgeClassifier.IsExpired(fact, now))
            return ToDto(fact, await NamesAsync(fact.VerifiedBy is { } by ? new List<Guid> { by } : new List<Guid>(), cancellationToken), now); // the first verification stands
        fact.Status = BusinessFactStatus.Verified;
        fact.VerifiedBy = actorUserId;
        fact.VerifiedAtUtc = now;
        fact.UpdatedBy = actorUserId;
        if (fact.ReviewEveryDays is { } days)
            fact.ExpiresAtUtc = now.AddDays(days);

        await BusinessFactHistory.RecordAsync(
            _context, fact, BusinessFactChangeKind.Verified, wasVerified ? "Confirmed again." : "Confirmed to be true.", actorUserId, false, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(fact, await NamesAsync(new List<Guid> { actorUserId }, cancellationToken), now);
    }

    public async Task<BusinessFactDto> RetireAsync(Guid tenantId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);

        var fact = await FindAsync(tenantId, id, cancellationToken);
        if (fact.Status != BusinessFactStatus.Retired)
        {
            fact.Status = BusinessFactStatus.Retired;
            fact.UpdatedBy = actorUserId;
            await BusinessFactHistory.RecordAsync(_context, fact, BusinessFactChangeKind.Retired, "Retired.", actorUserId, false, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ToDto(fact, new Dictionary<Guid, string>(), _dateTime.UtcNow);
    }

    public async Task<IReadOnlyList<BusinessFactRevisionDto>> GetHistoryAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        await FindAsync(tenantId, id, cancellationToken); // another tenant's fact answers as a missing one

        var rows = await _context.BusinessFactRevisions.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.FactId == id)
            .OrderByDescending(r => r.Number)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(rows.Where(r => r.ChangedBy != null).Select(r => r.ChangedBy!.Value).Distinct().ToList(), cancellationToken);

        return rows.Select(r => new BusinessFactRevisionDto(
            r.Number, r.Kind, r.Category, r.Subject, r.Statement, r.Context, r.Status, r.Note, r.ChangedBy,
            r.ChangedBy is { } by && names.TryGetValue(by, out var name) ? name : null, r.CreatedAt)).ToList();
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

    internal static void Validate(SaveBusinessFactRequest request)
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(request.Category) || request.Category.Trim().Length > MaxCategory)
            failures.Add(new ValidationFailure(nameof(request.Category), $"Give the fact a category of up to {MaxCategory} characters."));
        if (string.IsNullOrWhiteSpace(request.Statement) || request.Statement.Trim().Length > MaxStatement)
            failures.Add(new ValidationFailure(nameof(request.Statement), $"Write the fact in up to {MaxStatement} characters."));
        else if (QuestionnaireEngine.LooksLikeSecret(request.Statement))
            failures.Add(new ValidationFailure(nameof(request.Statement), QuestionnaireEngine.SecretMessage));
        if (Clean(request.Subject) is { Length: > MaxSubject })
            failures.Add(new ValidationFailure(nameof(request.Subject), $"Keep the subject to {MaxSubject} characters."));
        if (Clean(request.Context) is { Length: > MaxContext })
            failures.Add(new ValidationFailure(nameof(request.Context), $"Keep this to {MaxContext} characters."));
        if (Clean(request.Context) is { } context && QuestionnaireEngine.LooksLikeSecret(context))
            failures.Add(new ValidationFailure(nameof(request.Context), QuestionnaireEngine.SecretMessage));
        failures.AddRange(EvidenceFailures(Clean(request.Evidence)));
        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    internal static void ValidateEvidence(string? evidence)
    {
        var failures = EvidenceFailures(evidence).ToList();
        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    private static IEnumerable<ValidationFailure> EvidenceFailures(string? evidence)
    {
        if (evidence is { Length: > MaxEvidence })
            yield return new ValidationFailure("Evidence", $"Keep this to {MaxEvidence} characters.");
        else if (evidence is not null && QuestionnaireEngine.LooksLikeSecret(evidence))
            yield return new ValidationFailure("Evidence", QuestionnaireEngine.SecretMessage);
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static BusinessFactDto ToDto(BusinessFact f, IReadOnlyDictionary<Guid, string> names, DateTime now) => new(
        f.Id, f.Category, f.Statement, f.SourceType, f.SourceRef, f.Status, f.VerifiedBy,
        f.VerifiedBy is { } v && names.TryGetValue(v, out var name) ? name : null, f.VerifiedAtUtc, f.CreatedAt, f.UpdatedAt,
        f.Subject, f.Context, f.Evidence, f.Origin, KnowledgeClassifier.Classify(f, now), f.ExpiresAtUtc, f.ReviewEveryDays);
}
