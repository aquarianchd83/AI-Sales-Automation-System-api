using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using ValidationFailure = FluentValidation.Results.ValidationFailure;

namespace WhatsAppSalesAutomation.Application.Simulator;

public record SimulatorDummyRecordDto(
    Guid Id,
    SimulatorDataCategory Category,
    string Name,
    string? Value,
    string? BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? Phone,
    string? Email,
    string? Website,
    int? LeadScore,
    SimulatorLeadVariant? Variant,
    bool IsActive,
    int DisplayOrder,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record SaveSimulatorDummyRecordRequest(
    SimulatorDataCategory Category,
    string Name,
    string? Value,
    string? BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? Phone,
    string? Email,
    string? Website,
    int? LeadScore,
    SimulatorLeadVariant? Variant,
    bool IsActive,
    int DisplayOrder);

public record SimulatorKeyDto(SimulatorDataCategory Category, string Key, string DefaultValue, string Help);

public record SimulatorDummyDataDto(IReadOnlyList<SimulatorDummyRecordDto> Records, IReadOnlyList<SimulatorKeyDto> Keys);

public record RestoreSimulatorDefaultsResult(int Added);

public interface ISimulatorDummyDataService
{
    Task<SimulatorDummyDataDto> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SimulatorDummyRecordDto> CreateAsync(SaveSimulatorDummyRecordRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
    Task<SimulatorDummyRecordDto> UpdateAsync(Guid id, SaveSimulatorDummyRecordRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Adds back every starter row that is missing (matched by category and name); rows the admin edited are left alone.</summary>
    Task<RestoreSimulatorDefaultsResult> RestoreDefaultsAsync(Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}

/// <summary>The Simulator dummy data page: PlatformSuperAdmin add/edit/delete of what the simulators use.</summary>
public sealed class SimulatorDummyDataService : ISimulatorDummyDataService
{
    private readonly IApplicationDbContext _context;
    private readonly IPlatformAuditService _audit;
    private readonly ISimulatorDataProvider _provider;

    public SimulatorDummyDataService(IApplicationDbContext context, IPlatformAuditService audit, ISimulatorDataProvider provider)
    {
        _context = context;
        _audit = audit;
        _provider = provider;
    }

    public async Task<SimulatorDummyDataDto> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.SimulatorDummyRecords.AsNoTracking()
            .OrderBy(r => r.Category).ThenBy(r => r.DisplayOrder).ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        return new SimulatorDummyDataDto(
            rows.Select(ToDto).ToList(),
            SimulatorDataCatalog.Keys.Select(k => new SimulatorKeyDto(k.Category, k.Key, k.DefaultValue, k.Help)).ToList());
    }

    public async Task<SimulatorDummyRecordDto> CreateAsync(SaveSimulatorDummyRecordRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var name = Clean(request.Name)!;
        await EnsureKeyIsFreeAsync(request.Category, name, null, cancellationToken);

        var row = new SimulatorDummyRecord { Category = request.Category };
        Apply(row, request);
        _context.SimulatorDummyRecords.Add(row);
        await _context.SaveChangesAsync(cancellationToken);
        _provider.Invalidate();

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.SimulatorDataCreated, details: $"{row.Category}: {row.Name}", cancellationToken: cancellationToken);
        return ToDto(row);
    }

    public async Task<SimulatorDummyRecordDto> UpdateAsync(Guid id, SaveSimulatorDummyRecordRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var row = await _context.SimulatorDummyRecords.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(SimulatorDummyRecord), id);

        Validate(request);
        if (request.Category != row.Category)
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Category), "The kind of a row cannot be changed. Delete it and add a new one instead.") });
        await EnsureKeyIsFreeAsync(row.Category, Clean(request.Name)!, row.Id, cancellationToken);

        Apply(row, request);
        await _context.SaveChangesAsync(cancellationToken);
        _provider.Invalidate();

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.SimulatorDataUpdated, details: $"{row.Category}: {row.Name}", cancellationToken: cancellationToken);
        return ToDto(row);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var row = await _context.SimulatorDummyRecords.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(SimulatorDummyRecord), id);

        _context.SimulatorDummyRecords.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
        _provider.Invalidate();

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.SimulatorDataDeleted, details: $"{row.Category}: {row.Name}", cancellationToken: cancellationToken);
    }

    public async Task<RestoreSimulatorDefaultsResult> RestoreDefaultsAsync(Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var existing = (await _context.SimulatorDummyRecords.AsNoTracking().Select(r => new { r.Category, r.Name }).ToListAsync(cancellationToken))
            .Select(r => (r.Category, r.Name.Trim().ToLowerInvariant()))
            .ToHashSet();

        var missing = SimulatorDataCatalog.DefaultRecords().Where(r => !existing.Contains((r.Category, r.Name.ToLowerInvariant()))).ToList();
        if (missing.Count > 0)
        {
            _context.SimulatorDummyRecords.AddRange(missing);
            await _context.SaveChangesAsync(cancellationToken);
            _provider.Invalidate();
        }

        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.SimulatorDataRestored, details: $"{missing.Count} starter rows added back", cancellationToken: cancellationToken);
        return new RestoreSimulatorDefaultsResult(missing.Count);
    }

    private async Task EnsureKeyIsFreeAsync(SimulatorDataCategory category, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        // A keyed value must be unique, or the simulator could not tell which one to use. Businesses and numbers may repeat.
        if (!SimulatorDataCatalog.IsKeyed(category))
            return;

        var lowered = name.ToLowerInvariant();
        var taken = await _context.SimulatorDummyRecords
            .AnyAsync(r => r.Category == category && r.Name.ToLower() == lowered && (exceptId == null || r.Id != exceptId), cancellationToken);
        if (taken)
            throw new ValidationException(new[] { new ValidationFailure(nameof(SaveSimulatorDummyRecordRequest.Name), $"'{name}' already exists. Edit that row instead.") });
    }

    private static void Validate(SaveSimulatorDummyRecordRequest request)
    {
        var failures = new List<ValidationFailure>();
        var name = Clean(request.Name);

        if (!Enum.IsDefined(request.Category))
            failures.Add(new ValidationFailure(nameof(request.Category), "Choose a kind of dummy data."));
        if (name is null)
            failures.Add(new ValidationFailure(nameof(request.Name), request.Category switch
            {
                SimulatorDataCategory.LeadBusiness => "Enter the business name.",
                SimulatorDataCategory.DndNumber => "Enter the phone number.",
                _ => "Choose the key."
            }));
        else if (name.Length > 200)
            failures.Add(new ValidationFailure(nameof(request.Name), "Keep it to 200 characters."));

        if (SimulatorDataCatalog.IsKeyed(request.Category))
        {
            if (name is not null && SimulatorDataCatalog.Find(request.Category, name) is null)
                failures.Add(new ValidationFailure(nameof(request.Name), "Choose one of the listed keys - the simulator reads only those."));
            if (string.IsNullOrWhiteSpace(request.Value))
                failures.Add(new ValidationFailure(nameof(request.Value), "Enter the value."));
            else if (request.Value.Length > 4000)
                failures.Add(new ValidationFailure(nameof(request.Value), "Keep it to 4,000 characters."));
        }

        if (request.Category == SimulatorDataCategory.Setting && name is not null)
        {
            var value = request.Value?.Trim() ?? string.Empty;
            var isPercent = string.Equals(name, SimulatorDataCatalog.Settings.FlawedSharePercent, StringComparison.OrdinalIgnoreCase);
            if (isPercent && !(int.TryParse(value, out var p) && p is >= 0 and <= 90))
                failures.Add(new ValidationFailure(nameof(request.Value), "Enter a whole number from 0 to 90."));
            if (!isPercent && !bool.TryParse(value, out _))
                failures.Add(new ValidationFailure(nameof(request.Value), "Enter true or false."));
        }

        if (request.Category == SimulatorDataCategory.DndNumber && name is not null && SimulatorDataSnapshot.Digits(name).Length < 8)
            failures.Add(new ValidationFailure(nameof(request.Name), "Enter a phone number with at least 8 digits."));

        if (request.Category == SimulatorDataCategory.LeadBusiness)
        {
            if (SimulatorDataSnapshot.Digits(request.Phone).Length < 8)
                failures.Add(new ValidationFailure(nameof(request.Phone), "Enter a phone number with at least 8 digits."));
            if (request.LeadScore is < 0 or > 100)
                failures.Add(new ValidationFailure(nameof(request.LeadScore), "Lead score is 0 to 100."));
            if (request.Variant is { } v && !Enum.IsDefined(v))
                failures.Add(new ValidationFailure(nameof(request.Variant), "Choose how the business behaves."));
            if (Clean(request.Email) is { } email && !email.Contains('@'))
                failures.Add(new ValidationFailure(nameof(request.Email), "Enter a valid email."));
            if (Clean(request.Website) is { } site && !Uri.TryCreate(site, UriKind.Absolute, out _))
                failures.Add(new ValidationFailure(nameof(request.Website), "Enter a full website address, like https://name.example"));
            foreach (var (field, text) in new[] { (nameof(request.BusinessType), request.BusinessType), (nameof(request.ContactPerson), request.ContactPerson),
                         (nameof(request.Address), request.Address), (nameof(request.City), request.City), (nameof(request.Email), request.Email), (nameof(request.Website), request.Website) })
            {
                if (text?.Trim().Length > 300)
                    failures.Add(new ValidationFailure(field, "Keep it to 300 characters."));
            }
        }

        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    private static void Apply(SimulatorDummyRecord row, SaveSimulatorDummyRecordRequest request)
    {
        var keyed = SimulatorDataCatalog.IsKeyed(request.Category);
        var business = request.Category == SimulatorDataCategory.LeadBusiness;

        // A key is stored in its catalog spelling, so the simulator's lookup always matches.
        row.Name = keyed ? SimulatorDataCatalog.Find(request.Category, Clean(request.Name)!)!.Key : Clean(request.Name)!;
        row.Value = keyed ? request.Value!.Trim() : Clean(request.Value);
        row.BusinessType = business ? Clean(request.BusinessType) : null;
        row.ContactPerson = business ? Clean(request.ContactPerson) : null;
        row.Address = business ? Clean(request.Address) : null;
        row.City = business ? Clean(request.City) : null;
        row.Phone = business ? Clean(request.Phone) : null;
        row.Email = business ? Clean(request.Email) : null;
        row.Website = business ? Clean(request.Website) : null;
        row.LeadScore = business ? request.LeadScore ?? 75 : null;
        row.Variant = business ? request.Variant ?? SimulatorLeadVariant.Qualified : null;
        row.IsActive = request.IsActive;
        row.DisplayOrder = request.DisplayOrder;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SimulatorDummyRecordDto ToDto(SimulatorDummyRecord r) => new(
        r.Id, r.Category, r.Name, r.Value, r.BusinessType, r.ContactPerson, r.Address, r.City, r.Phone, r.Email, r.Website,
        r.LeadScore, r.Variant, r.IsActive, r.DisplayOrder, r.CreatedAt, r.UpdatedAt);
}
