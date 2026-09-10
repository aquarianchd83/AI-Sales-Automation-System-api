using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>DI-facing implementation of <see cref="ITenantWhatsAppConfigProvider"/> - EF Core against
/// the same ApplicationDbContext everything else uses, same pattern as AppSettingsStore. Depends on
/// the concrete ApplicationDbContext (not IApplicationDbContext) because TenantWhatsAppConfig is
/// deliberately not exposed there - see that entity's own doc comment.</summary>
public class TenantWhatsAppConfigProvider : ITenantWhatsAppConfigProvider
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<UpdateTenantWhatsAppConfigRequest> _saveValidator;

    // Memoized per instance (this is registered Scoped - one per request/job), not per call: every
    // WhatsAppServiceFactory method resolves credentials fresh "per call" in the sense of never
    // trusting a value cached from an earlier scope, but within one scope there is exactly one DB
    // round trip to pay, not one per retry/component.
    private Task<TenantWhatsAppCredentials?>? _cachedCredentials;

    public TenantWhatsAppConfigProvider(
        ApplicationDbContext context,
        ITenantContext tenantContext,
        IDataProtectionProvider dataProtectionProvider,
        IDateTimeProvider dateTime,
        IValidator<UpdateTenantWhatsAppConfigRequest> saveValidator)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dataProtectionProvider = dataProtectionProvider;
        _dateTime = dateTime;
        _saveValidator = saveValidator;
    }

    public Task<TenantWhatsAppCredentials?> GetForCurrentTenantAsync(CancellationToken cancellationToken = default)
        => _cachedCredentials ??= LoadForCurrentTenantAsync();

    private async Task<TenantWhatsAppCredentials?> LoadForCurrentTenantAsync()
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return null;

        // Ordinary tenant-scoped read: the reflective ITenantOwned filter already restricts this to
        // the caller's own tenant, so no explicit TenantId comparison is needed (or would be wrong to
        // add - see GetByPhoneNumberIdAsync for the one place that deliberately bypasses it instead).
        var row = await _context.TenantWhatsAppConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId);
        return row is null ? null : Decrypt(row);
    }

    public async Task<TenantWhatsAppLookupResult?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken = default)
    {
        // The one deliberately cross-tenant lookup - see this method's own interface doc comment.
        // IgnoreQueryFilters() bypasses the reflective ITenantOwned filter (which would otherwise
        // evaluate to "TenantId == null" for this anonymous request and find nothing, no matter which
        // tenant actually owns the row - the exact class of bug fixed on ApplicationUser in Phase 1).
        var row = await _context.TenantWhatsAppConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.PhoneNumberId == phoneNumberId, cancellationToken);

        return row is null ? null : new TenantWhatsAppLookupResult(row.TenantId, Decrypt(row));
    }

    public Task<TenantWhatsAppConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default) =>
        _tenantContext.TenantId is { } tenantId ? GetConfigForTenantAsync(tenantId, cancellationToken) : Task.FromResult<TenantWhatsAppConfigDto?>(null);

    /// <summary>Deprecated - see this method's own interface doc comment. Still resolves the ambient
    /// tenant and delegates to <see cref="SaveConfigForTenantAsync"/> rather than duplicating the save
    /// logic, but nothing in this codebase calls it anymore now that TenantSettingsController's PUT
    /// endpoints are gone.</summary>
    public async Task<TenantWhatsAppConfigDto> SaveConfigForCurrentTenantAsync(
        UpdateTenantWhatsAppConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            throw new InvalidOperationException("Cannot save WhatsApp config without a tenant in scope.");

        var dto = await SaveConfigForTenantAsync(tenantId, request, updatedByUserId, cancellationToken);

        // Invalidate the memoized read - a save-then-immediately-use within the same scope (unlikely
        // today, but cheap to make safe) must see the fresh row, not whatever GetForCurrentTenantAsync
        // may have already cached (including a cached "null" from before this row existed).
        _cachedCredentials = null;

        return dto;
    }

    public async Task<TenantWhatsAppConfigDto?> GetConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters() - the caller is a PlatformSuperAdmin (Platform Admin Console) or the
        // ambient-tenant delegation above, neither of which is guaranteed to have tenantId as the
        // reflective ITenantOwned filter's ambient tenant, so the filter can't be relied on here the
        // way GetForCurrentTenantAsync's ordinary tenant-scoped read does.
        var row = await _context.TenantWhatsAppConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public async Task<TenantWhatsAppConfigDto> SaveConfigForTenantAsync(
        Guid tenantId, UpdateTenantWhatsAppConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _saveValidator.ValidateAndThrowAsync(request, cancellationToken);

        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);

        var row = await _context.TenantWhatsAppConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            row = new TenantWhatsAppConfig { TenantId = tenantId };
            _context.TenantWhatsAppConfigs.Add(row);
        }

        row.PhoneNumberId = request.PhoneNumberId.Trim();
        row.WhatsAppBusinessAccountId = request.WhatsAppBusinessAccountId.Trim();
        // Null leaves the stored ciphertext untouched (see UpdateTenantWhatsAppConfigRequest's own
        // doc comment); an empty string is the caller's explicit way to clear it.
        if (request.AccessToken is not null)
            row.AccessToken = request.AccessToken.Length == 0 ? null : protector.Protect(request.AccessToken);
        if (request.AppSecret is not null)
            row.AppSecret = request.AppSecret.Length == 0 ? null : protector.Protect(request.AppSecret);
        if (request.WebhookVerifyToken is not null)
            row.WebhookVerifyToken = request.WebhookVerifyToken.Length == 0 ? null : protector.Protect(request.WebhookVerifyToken);
        if (!string.IsNullOrWhiteSpace(request.ApiVersion))
            row.ApiVersion = request.ApiVersion.Trim();
        if (!string.IsNullOrWhiteSpace(request.ApiBaseUrl))
            row.ApiBaseUrl = request.ApiBaseUrl.Trim();
        // Not a secret, plain text - same "blank leaves it unchanged" convention as ApiVersion/
        // ApiBaseUrl above, not the "empty explicitly clears" convention the three secrets use.
        if (!string.IsNullOrWhiteSpace(request.AppId))
            row.AppId = request.AppId.Trim();

        row.IsConnected = !string.IsNullOrWhiteSpace(row.PhoneNumberId) && row.AccessToken is not null && row.AppSecret is not null;
        row.UpdatedAtUtc = _dateTime.UtcNow;
        row.UpdatedByUserId = updatedByUserId;

        await _context.SaveChangesAsync(cancellationToken);

        if (_tenantContext.TenantId == tenantId)
            _cachedCredentials = null;

        return ToDto(row);
    }

    public async Task DeleteConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await _context.TenantWhatsAppConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
            return;

        _context.TenantWhatsAppConfigs.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);

        if (_tenantContext.TenantId == tenantId)
            _cachedCredentials = null;
    }

    public async Task<IReadOnlyList<TenantWhatsAppConnectionSummary>> GetAllConnectionSummariesAsync(CancellationToken cancellationToken = default)
    {
        // Cross-tenant by design - see this method's own interface doc comment, same IgnoreQueryFilters()
        // reasoning as GetByPhoneNumberIdAsync. Only PhoneNumberId/WhatsAppBusinessAccountId/IsConnected/
        // UpdatedAtUtc are selected - never AccessToken/AppSecret ciphertext, this never needs decrypting.
        return await _context.TenantWhatsAppConfigs.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new TenantWhatsAppConnectionSummary(c.TenantId, c.PhoneNumberId, c.WhatsAppBusinessAccountId, c.IsConnected, c.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private TenantWhatsAppCredentials Decrypt(TenantWhatsAppConfig row)
    {
        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);
        return new TenantWhatsAppCredentials(
            row.PhoneNumberId,
            row.WhatsAppBusinessAccountId,
            TryUnprotect(protector, row.AccessToken),
            TryUnprotect(protector, row.AppSecret),
            row.ApiVersion,
            row.ApiBaseUrl);
    }

    private static TenantWhatsAppConfigDto ToDto(TenantWhatsAppConfig row) => new(
        row.PhoneNumberId,
        row.WhatsAppBusinessAccountId,
        row.AccessToken is not null,
        row.AppSecret is not null,
        row.ApiVersion,
        row.ApiBaseUrl,
        row.IsConnected,
        row.WebhookVerifyToken is not null,
        row.AppId);

    private static string TryUnprotect(IDataProtector protector, string? ciphertext)
    {
        if (ciphertext is null)
            return string.Empty;

        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch
        {
            // Same "corrupt/undecryptable value behaves as unset" tolerance as AppSettingsStore's own
            // TryUnprotect - a key-ring rotation or manual DB edit should degrade to "not configured",
            // not throw and take the whole webhook/send pipeline down with it.
            return string.Empty;
        }
    }
}
