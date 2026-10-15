using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Customers;

public class CustomerService : ICustomerService
{
    private readonly IApplicationDbContext _context;
    private readonly ICustomerImportService _importService;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<CreateCustomerRequest> _createValidator;
    private readonly IValidator<UpdateCustomerRequest> _updateValidator;
    private readonly IValidator<BulkDeleteCustomersRequest> _bulkDeleteValidator;
    private readonly IValidator<OptInCustomerRequest> _optInValidator;
    private readonly IValidator<BulkAddCustomerTagsRequest> _bulkTagsValidator;
    private readonly ITenantContext _tenantContext;

    public CustomerService(
        IApplicationDbContext context,
        ICustomerImportService importService,
        IDateTimeProvider dateTime,
        IValidator<CreateCustomerRequest> createValidator,
        IValidator<UpdateCustomerRequest> updateValidator,
        IValidator<BulkDeleteCustomersRequest> bulkDeleteValidator,
        IValidator<OptInCustomerRequest> optInValidator,
        IValidator<BulkAddCustomerTagsRequest> bulkTagsValidator,
        ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
        _context = context;
        _importService = importService;
        _dateTime = dateTime;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _bulkDeleteValidator = bulkDeleteValidator;
        _optInValidator = optInValidator;
        _bulkTagsValidator = bulkTagsValidator;
    }

    public async Task<PagedResult<CustomerDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.Customers.Include(c => c.Tags).AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(c =>
                c.PhoneNumberE164.Contains(search) ||
                (c.FirstName != null && c.FirstName.Contains(search)) ||
                (c.LastName != null && c.LastName.Contains(search)) ||
                (c.Email != null && c.Email.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CustomerDto>(items.Select(c => c.ToDto()).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<CustomerDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        return customer.ToDto();
    }

    public async Task<IReadOnlyList<CustomerHistoryEventDto>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Both queries go through the tenant filter, so another tenant's customer is a 404, never an empty timeline.
        if (!await _context.Customers.AnyAsync(c => c.Id == id, cancellationToken))
            throw new NotFoundException(nameof(Customer), id);

        var events = await _context.CustomerHistoryEvents.AsNoTracking()
            .Where(e => e.CustomerId == id)
            .ToListAsync(cancellationToken);

        return OrderTimeline(events);
    }

    /// <summary>
    /// The timeline, top to bottom: the initial step (Customer discovered) first, then the rest oldest first, ties broken by the order
    /// the process runs in and then by when they were saved. A discovered event written after the check it preceded (history recorded
    /// before the order was fixed) is shown at the time of the first event after it, so no entry is dated later than the one below it.
    /// </summary>
    private static List<CustomerHistoryEventDto> OrderTimeline(IReadOnlyCollection<CustomerHistoryEvent> events)
    {
        var discovered = events.Where(e => e.EventType == CustomerHistoryEventTypes.Discovered)
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.CreatedAt).ToList();
        var rest = events.Where(e => e.EventType != CustomerHistoryEventTypes.Discovered)
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => CustomerHistoryEventTypes.Rank(e.EventType)).ThenBy(e => e.CreatedAt).ToList();

        var firstAfter = rest.Count > 0 ? rest[0].OccurredAtUtc : (DateTime?)null;
        return discovered.Concat(rest)
            .Select(e => new CustomerHistoryEventDto(
                e.Id, e.EventType, e.EventType == CustomerHistoryEventTypes.Discovered && firstAfter is { } next && next < e.OccurredAtUtc ? next : e.OccurredAtUtc,
                e.PreviousStatus, e.NewStatus, e.Reason, e.ExecutionId, e.CampaignId, e.CorrelationId, e.ProviderReference))
            .ToList();
    }

    public async Task<CustomerConsentSettingDto> GetConsentSettingAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId ?? throw new ConflictException("There is no tenant to read this setting for.");
        var required = await _context.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.ConsentRequired)
            .FirstOrDefaultAsync(cancellationToken);
        return new CustomerConsentSettingDto(required);
    }

    public async Task<CustomerConsentSettingDto> SetConsentSettingAsync(
        SetCustomerConsentSettingRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId ?? throw new ConflictException("There is no tenant to change this setting for.");
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
                     ?? throw new NotFoundException("Tenant", tenantId);

        // Applies to customers created from now on; customers that already exist keep the status they have.
        tenant.ConsentRequired = request.ConsentRequired;
        await _context.SaveChangesAsync(cancellationToken);
        return new CustomerConsentSettingDto(tenant.ConsentRequired);
    }

    public async Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        // Validation has already proven this succeeds; normalize so "9820098200" and
        // "+91 98200-98200" cannot both be stored as separate customers.
        PhoneNumberNormalizer.TryNormalize(request.PhoneNumberE164, out var phoneNumber);

        await EnsurePhoneNumberIsFreeAsync(phoneNumber, excludingCustomerId: null, cancellationToken);

        var customer = new Customer
        {
            PhoneNumberE164 = phoneNumber,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Source = request.Source,
            PreferredLanguage = request.PreferredLanguage,
            AssignedAgentId = request.AssignedAgentId,
            OptInStatus = await ConsentPolicy.InitialStatusAsync(_context, _tenantContext.TenantId, cancellationToken)
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task<CustomerDto> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        PhoneNumberNormalizer.TryNormalize(request.PhoneNumberE164, out var phoneNumber);

        if (phoneNumber != customer.PhoneNumberE164)
        {
            await EnsurePhoneNumberIsFreeAsync(phoneNumber, excludingCustomerId: id, cancellationToken);
            customer.PhoneNumberE164 = phoneNumber;
        }

        customer.FirstName = request.FirstName;
        customer.LastName = request.LastName;
        customer.Email = request.Email;
        customer.Source = request.Source;
        customer.PreferredLanguage = request.PreferredLanguage;
        customer.AssignedAgentId = request.AssignedAgentId;

        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        customer.IsDeleted = true;
        customer.DeletedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Guards the unique index on (TenantId, PhoneNumberE164) with a readable 409 instead of a raw
    /// DbUpdateException. The index is filtered to non-deleted rows (see CustomerConfiguration), so
    /// this deliberately uses the default query filter too - a soft-deleted customer's number is
    /// free for reuse, same as the database will actually allow.
    /// </summary>
    private async Task EnsurePhoneNumberIsFreeAsync(string phoneNumber, Guid? excludingCustomerId, CancellationToken cancellationToken)
    {
        var clash = await _context.Customers
            .AnyAsync(c => c.PhoneNumberE164 == phoneNumber && (excludingCustomerId == null || c.Id != excludingCustomerId), cancellationToken);

        if (clash)
            throw new ConflictException($"A customer with phone number '{phoneNumber}' already exists.");
    }

    public async Task<BulkDeleteCustomersResultDto> BulkDeleteAsync(BulkDeleteCustomersRequest request, CancellationToken cancellationToken = default)
    {
        await _bulkDeleteValidator.ValidateAndThrowAsync(request, cancellationToken);

        // Distinct so a caller repeating an id cannot inflate RequestedCount past what was deleted.
        var ids = request.Ids.Distinct().ToList();

        // One query, one UPDATE batch - the single-id DeleteAsync in a loop would be N round trips.
        // The global query filter means already-deleted rows are not returned, so they fall through
        // to NotFoundIds and the operation stays idempotent on repeat calls.
        var customers = await _context.Customers
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken);

        var now = _dateTime.UtcNow;
        foreach (var customer in customers)
        {
            customer.IsDeleted = true;
            customer.DeletedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        var foundIds = customers.Select(c => c.Id).ToHashSet();
        var notFound = ids.Where(id => !foundIds.Contains(id)).ToList();

        return new BulkDeleteCustomersResultDto(ids.Count, customers.Count, notFound);
    }

    public async Task<BulkAddCustomerTagsResultDto> BulkAddTagsAsync(BulkAddCustomerTagsRequest request, CancellationToken cancellationToken = default)
    {
        await _bulkTagsValidator.ValidateAndThrowAsync(request, cancellationToken);

        var ids = request.Ids.Distinct().ToList();
        var tagNames = request.TagNames
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The global query filters scope this to the tenant and hide soft-deleted rows, so another tenant's or an already-deleted id
        // falls through to NotFoundIds exactly as it does for bulk delete.
        var customers = await _context.Customers.Include(c => c.Tags)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken);

        // Each tag is resolved (or created) once, not once per customer.
        var tags = new List<CustomerTag>();
        foreach (var tagName in tagNames)
        {
            var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower(), cancellationToken);
            if (tag is null)
            {
                tag = new CustomerTag { Name = tagName };
                _context.CustomerTags.Add(tag);
            }

            tags.Add(tag);
        }

        var updated = 0;
        foreach (var customer in customers)
        {
            var gained = false;
            foreach (var tag in tags)
            {
                if (customer.Tags.All(t => t.Id != tag.Id))
                {
                    customer.Tags.Add(tag);
                    gained = true;
                }
            }

            if (gained)
                updated++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        var foundIds = customers.Select(c => c.Id).ToHashSet();
        var notFound = ids.Where(id => !foundIds.Contains(id)).ToList();

        return new BulkAddCustomerTagsResultDto(ids.Count, updated, notFound, tagNames);
    }

    public async Task<CustomerDto> AddTagsAsync(Guid id, AddCustomerTagsRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        var requestedNames = request.TagNames
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in requestedNames)
        {
            var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower(), cancellationToken);
            if (tag is null)
            {
                tag = new CustomerTag { Name = tagName };
                _context.CustomerTags.Add(tag);
            }

            if (customer.Tags.All(t => t.Id != tag.Id))
                customer.Tags.Add(tag);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task<CustomerDto> RemoveTagAsync(Guid id, string tagName, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        // Only detaches the join row - the CustomerTag itself stays, since other customers may
        // still reference it. A no-op (not an error) if the customer never had it, matching
        // AddTagsAsync's own tolerance for redundant calls.
        var tag = customer.Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
        if (tag is not null)
        {
            customer.Tags.Remove(tag);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return customer.ToDto();
    }

    public async Task<CustomerDto> OptInAsync(Guid id, OptInCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await _optInValidator.ValidateAndThrowAsync(request, cancellationToken);

        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        // Idempotent: the first consent record is the evidence, so a repeat call must not overwrite
        // its timestamp with a later one. A customer who previously opted out may consent again -
        // OptOutTimestamp is left in place as history until Phase 6's AuditLogs carry the full trail.
        if (customer.OptInStatus != OptInStatus.OptedIn)
        {
            customer.OptInStatus = OptInStatus.OptedIn;
            customer.OptInTimestamp = request.CapturedAt ?? _dateTime.UtcNow;
            customer.OptInSource = request.Source.Trim();
            customer.OptInProof = request.Proof!.Trim();
            // Already validated by OptInCustomerRequestValidator.
            ImportConsentResolver.TryParseScope(request.Scope, out var scope);
            customer.OptInScope = scope;

            await _context.SaveChangesAsync(cancellationToken);
        }

        return customer.ToDto();
    }

    public async Task<CustomerDto> OptOutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers.Include(c => c.Tags)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), id);

        customer.OptInStatus = OptInStatus.OptedOut;
        customer.OptOutTimestamp = _dateTime.UtcNow;
        // A person did this, not a keyword or the model. The distinction is the whole point of the
        // field: a compliance review asking "did they really ask us to stop?" reads a Manual opt-out
        // very differently from an AiDetected one.
        customer.OptOutSource = OptOutSource.Manual;

        await _context.SaveChangesAsync(cancellationToken);

        return customer.ToDto();
    }

    public async Task<CustomerImportResultDto> ImportAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var rows = await _importService.ParseAsync(fileStream, fileName, cancellationToken);
        var errors = new List<CustomerImportRowError>();
        var imported = 0;
        var skipped = 0;

        // Numbers already staged by an earlier row in this same file. Nothing is written until the
        // single SaveChanges below, so the AnyAsync check cannot see them - and after normalization
        // two differently-typed rows ("9820098200" and "+91 98200-98200") collapse to one number,
        // which would otherwise reach the unique index and fail the entire import.
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);

        // What a row with no consent of its own starts as, from the tenant's consent-required setting.
        var initialStatus = await ConsentPolicy.InitialStatusAsync(_context, _tenantContext.TenantId, cancellationToken);

        // Note: this checks/inserts row-by-row for correctness and simplicity. For very large
        // imports (10k+ rows) this is a good candidate to batch-load existing phone numbers and
        // tags up front - flagged here rather than optimized prematurely.
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Spreadsheets rarely hold clean E.164. Indian numbers arrive as 9820098200,
            // 09820098200 or 91 98200 98200, and Excel silently drops a leading 0 or + from a
            // numeric cell, so normalize rather than reject.
            if (!PhoneNumberNormalizer.TryNormalize(row.PhoneNumber, out var phoneNumber))
            {
                errors.Add(new CustomerImportRowError(
                    row.RowNumber,
                    $"Invalid or missing phone number '{row.PhoneNumber}'. Expected a 10-digit Indian mobile number, e.g. 9820098200 or +919820098200."));
                continue;
            }

            if (!ImportConsentResolver.TryResolve(row, _dateTime.UtcNow, out var consent, out var consentError))
            {
                errors.Add(new CustomerImportRowError(row.RowNumber, consentError));
                continue;
            }

            if (!seenInFile.Add(phoneNumber))
            {
                skipped++;
                continue;
            }

            // IgnoreQueryFilters: a soft-deleted customer still holds the number in the unique
            // index, so skip it rather than letting the insert fail the whole import.
            var exists = await _context.Customers
                .IgnoreQueryFilters()
                .AnyAsync(c => c.PhoneNumberE164 == phoneNumber, cancellationToken);
            if (exists)
            {
                skipped++;
                continue;
            }

            var customer = new Customer
            {
                PhoneNumberE164 = phoneNumber,
                FirstName = row.FirstName,
                LastName = row.LastName,
                Email = row.Email,
                Source = "Import",
                OptInStatus = consent.Status == OptInStatus.PendingOptIn ? initialStatus : consent.Status,
                OptInTimestamp = consent.OptInTimestamp,
                OptInSource = consent.Source,
                OptInProof = consent.Proof,
                OptInScope = consent.Scope,
                OptOutTimestamp = consent.OptOutTimestamp,
                OptOutSource = consent.Status == OptInStatus.OptedOut ? OptOutSource.Manual : null
            };

            foreach (var tagName in row.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower(), cancellationToken);
                if (tag is null)
                {
                    tag = new CustomerTag { Name = tagName };
                    _context.CustomerTags.Add(tag);
                }

                customer.Tags.Add(tag);
            }

            _context.Customers.Add(customer);
            imported++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new CustomerImportResultDto(rows.Count, imported, skipped, errors.Count, errors);
    }
}
