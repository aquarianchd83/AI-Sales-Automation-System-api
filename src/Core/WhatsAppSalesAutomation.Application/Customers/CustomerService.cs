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

    public CustomerService(
        IApplicationDbContext context,
        ICustomerImportService importService,
        IDateTimeProvider dateTime,
        IValidator<CreateCustomerRequest> createValidator,
        IValidator<UpdateCustomerRequest> updateValidator,
        IValidator<BulkDeleteCustomersRequest> bulkDeleteValidator,
        IValidator<OptInCustomerRequest> optInValidator)
    {
        _context = context;
        _importService = importService;
        _dateTime = dateTime;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _bulkDeleteValidator = bulkDeleteValidator;
        _optInValidator = optInValidator;
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
            OptInStatus = OptInStatus.PendingOptIn
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
    /// Guards the unique index on PhoneNumberE164 with a readable 409 instead of a raw
    /// DbUpdateException. IgnoreQueryFilters matters: a soft-deleted customer still occupies the
    /// number as far as the index is concerned, even though normal queries cannot see it.
    /// </summary>
    private async Task EnsurePhoneNumberIsFreeAsync(string phoneNumber, Guid? excludingCustomerId, CancellationToken cancellationToken)
    {
        var clash = await _context.Customers
            .IgnoreQueryFilters()
            .Where(c => c.PhoneNumberE164 == phoneNumber && (excludingCustomerId == null || c.Id != excludingCustomerId))
            .Select(c => new { c.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);

        if (clash is null)
            return;

        throw new ConflictException(clash.IsDeleted
            ? $"Phone number '{phoneNumber}' belongs to a deleted customer. Restore that record instead of creating a duplicate."
            : $"A customer with phone number '{phoneNumber}' already exists.");
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
                OptInStatus = consent.Status,
                OptInTimestamp = consent.OptInTimestamp,
                OptInSource = consent.Source,
                OptOutTimestamp = consent.OptOutTimestamp
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
