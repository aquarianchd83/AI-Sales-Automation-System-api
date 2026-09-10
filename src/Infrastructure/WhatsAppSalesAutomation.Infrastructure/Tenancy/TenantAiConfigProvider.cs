using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>DI-facing implementation of <see cref="ITenantAiConfigProvider"/> - see
/// <see cref="TenantWhatsAppConfigProvider"/>'s own doc comment for the identical pattern/reasoning.</summary>
public class TenantAiConfigProvider : ITenantAiConfigProvider
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<UpdateTenantAiProviderConfigRequest> _saveValidator;

    // See TenantWhatsAppConfigProvider's identical field for why this is memoized per-scope rather
    // than per-call - this is also what lets IEmbeddingService/IEmbeddingProviderCatalog/IAiService's
    // synchronous properties (ProviderName/ModelName/IsAvailable/ActiveProvider) block on this task
    // without paying a real DB round trip more than once per scope.
    private Task<TenantAiCredentials?>? _cachedCredentials;

    public TenantAiConfigProvider(
        ApplicationDbContext context,
        ITenantContext tenantContext,
        IDataProtectionProvider dataProtectionProvider,
        IDateTimeProvider dateTime,
        IValidator<UpdateTenantAiProviderConfigRequest> saveValidator)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dataProtectionProvider = dataProtectionProvider;
        _dateTime = dateTime;
        _saveValidator = saveValidator;
    }

    public Task<TenantAiCredentials?> GetForCurrentTenantAsync(CancellationToken cancellationToken = default)
        => _cachedCredentials ??= LoadForCurrentTenantAsync();

    private async Task<TenantAiCredentials?> LoadForCurrentTenantAsync()
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return null;

        var row = await _context.TenantAiProviderConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId);
        return row is null ? null : Decrypt(row);
    }

    public Task<TenantAiProviderConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default) =>
        _tenantContext.TenantId is { } tenantId ? GetConfigForTenantAsync(tenantId, cancellationToken) : Task.FromResult<TenantAiProviderConfigDto?>(null);

    /// <summary>Deprecated - see this method's own interface doc comment. Still resolves the ambient
    /// tenant and delegates to <see cref="SaveConfigForTenantAsync"/> rather than duplicating the save
    /// logic, but nothing in this codebase calls it anymore now that TenantSettingsController's PUT
    /// endpoints are gone.</summary>
    public async Task<TenantAiProviderConfigDto> SaveConfigForCurrentTenantAsync(
        UpdateTenantAiProviderConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            throw new InvalidOperationException("Cannot save AI provider config without a tenant in scope.");

        var dto = await SaveConfigForTenantAsync(tenantId, request, updatedByUserId, cancellationToken);
        _cachedCredentials = null;
        return dto;
    }

    public async Task<TenantAiProviderConfigDto?> GetConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters() - see TenantWhatsAppConfigProvider.GetConfigForTenantAsync's identical
        // reasoning: the caller here is a PlatformSuperAdmin (no ambient tenant) or the delegation
        // above, neither of which can rely on the reflective ITenantOwned filter matching tenantId.
        var row = await _context.TenantAiProviderConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public async Task<TenantAiProviderConfigDto> SaveConfigForTenantAsync(
        Guid tenantId, UpdateTenantAiProviderConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _saveValidator.ValidateAndThrowAsync(request, cancellationToken);

        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);

        var row = await _context.TenantAiProviderConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            row = new TenantAiProviderConfig { TenantId = tenantId };
            _context.TenantAiProviderConfigs.Add(row);
        }

        if (!string.IsNullOrWhiteSpace(request.Provider))
            row.Provider = request.Provider.Trim();
        if (!string.IsNullOrWhiteSpace(request.EmbeddingProvider))
            row.EmbeddingProvider = request.EmbeddingProvider.Trim();

        // Null leaves the stored ciphertext untouched, empty string explicitly clears it - same
        // convention as TenantWhatsAppConfigProvider.SaveConfigForTenantAsync.
        if (request.AnthropicApiKey is not null)
            row.AnthropicApiKey = request.AnthropicApiKey.Length == 0 ? null : protector.Protect(request.AnthropicApiKey);
        if (!string.IsNullOrWhiteSpace(request.AnthropicModel))
            row.AnthropicModel = request.AnthropicModel.Trim();

        if (request.OpenAiApiKey is not null)
            row.OpenAiApiKey = request.OpenAiApiKey.Length == 0 ? null : protector.Protect(request.OpenAiApiKey);
        if (!string.IsNullOrWhiteSpace(request.OpenAiChatModel))
            row.OpenAiChatModel = request.OpenAiChatModel.Trim();
        if (!string.IsNullOrWhiteSpace(request.OpenAiEmbeddingModel))
            row.OpenAiEmbeddingModel = request.OpenAiEmbeddingModel.Trim();

        if (request.GoogleApiKey is not null)
            row.GoogleApiKey = request.GoogleApiKey.Length == 0 ? null : protector.Protect(request.GoogleApiKey);
        if (!string.IsNullOrWhiteSpace(request.GoogleChatModel))
            row.GoogleChatModel = request.GoogleChatModel.Trim();
        if (!string.IsNullOrWhiteSpace(request.GoogleEmbeddingModel))
            row.GoogleEmbeddingModel = request.GoogleEmbeddingModel.Trim();

        row.UpdatedAtUtc = _dateTime.UtcNow;
        row.UpdatedByUserId = updatedByUserId;

        await _context.SaveChangesAsync(cancellationToken);

        if (_tenantContext.TenantId == tenantId)
            _cachedCredentials = null;

        return ToDto(row);
    }

    public async Task DeleteConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await _context.TenantAiProviderConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
            return;

        _context.TenantAiProviderConfigs.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);

        if (_tenantContext.TenantId == tenantId)
            _cachedCredentials = null;
    }

    private TenantAiCredentials Decrypt(TenantAiProviderConfig row)
    {
        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);
        return new TenantAiCredentials(
            row.Provider,
            row.EmbeddingProvider,
            TryUnprotect(protector, row.AnthropicApiKey), row.AnthropicModel, row.AnthropicApiVersion, row.AnthropicBaseUrl,
            TryUnprotect(protector, row.OpenAiApiKey), row.OpenAiChatModel, row.OpenAiEmbeddingModel, row.OpenAiBaseUrl,
            TryUnprotect(protector, row.GoogleApiKey), row.GoogleChatModel, row.GoogleEmbeddingModel, row.GoogleBaseUrl);
    }

    private static TenantAiProviderConfigDto ToDto(TenantAiProviderConfig row) => new(
        row.Provider,
        row.EmbeddingProvider,
        row.AnthropicApiKey is not null, row.AnthropicModel,
        row.OpenAiApiKey is not null, row.OpenAiChatModel, row.OpenAiEmbeddingModel,
        row.GoogleApiKey is not null, row.GoogleChatModel, row.GoogleEmbeddingModel);

    private static string? TryUnprotect(IDataProtector protector, string? ciphertext)
    {
        if (ciphertext is null)
            return null;

        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch
        {
            // See TenantWhatsAppConfigProvider.TryUnprotect's identical reasoning.
            return null;
        }
    }
}
