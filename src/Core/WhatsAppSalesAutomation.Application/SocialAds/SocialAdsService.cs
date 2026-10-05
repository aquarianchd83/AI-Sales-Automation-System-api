using System.Globalization;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.SocialAds;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.SocialAds;

public class SocialAdsService : ISocialAdsService
{
    /// <summary>How long the Facebook login "state" stays valid - long enough to log in, short enough to be useless later.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    /// <summary>A "Sync now" within this window of the last sync is answered from what is stored.</summary>
    public static readonly TimeSpan SyncCooldown = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly IMetaAdsClient _meta;
    private readonly ISecretProtector _protector;
    private readonly ISocialAdSyncService _sync;
    private readonly IDateTimeProvider _clock;
    private readonly IValidator<CompleteSocialAdsConnectRequest> _connectValidator;
    private readonly IValidator<SelectSocialAdAccountRequest> _selectValidator;
    private readonly IValidator<SaveManualAdSpendRequest> _manualValidator;

    public SocialAdsService(
        IApplicationDbContext context,
        ITenantContext tenant,
        IMetaAdsClient meta,
        ISecretProtector protector,
        ISocialAdSyncService sync,
        IDateTimeProvider clock,
        IValidator<CompleteSocialAdsConnectRequest> connectValidator,
        IValidator<SelectSocialAdAccountRequest> selectValidator,
        IValidator<SaveManualAdSpendRequest> manualValidator)
    {
        _context = context;
        _tenant = tenant;
        _meta = meta;
        _protector = protector;
        _sync = sync;
        _clock = clock;
        _connectValidator = connectValidator;
        _selectValidator = selectValidator;
        _manualValidator = manualValidator;
    }

    private Guid TenantId => _tenant.TenantId ?? throw new InvalidOperationException("Social ads request has no tenant in scope.");

    public async Task<SocialAdsStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.SocialAdConnections.FirstOrDefaultAsync(cancellationToken);
        return ToStatus(connection);
    }

    public Task<SocialAdsConnectUrlDto> GetConnectUrlAsync(string redirectUri, CancellationToken cancellationToken = default)
    {
        if (!_meta.IsConfigured)
            throw new ConflictException("Connecting Facebook is not set up on this platform yet. Ask your platform administrator to add the Meta App.");

        EnsureValidRedirect(redirectUri);

        var state = _protector.Protect($"{TenantId:N}|{(_clock.UtcNow + StateLifetime).Ticks}");
        return Task.FromResult(new SocialAdsConnectUrlDto(_meta.BuildLoginUrl(redirectUri, state)));
    }

    public async Task<SocialAdsStatusDto> CompleteConnectAsync(CompleteSocialAdsConnectRequest request, CancellationToken cancellationToken = default)
    {
        await _connectValidator.ValidateAndThrowAsync(request, cancellationToken);
        if (!_meta.IsConfigured)
            throw new ConflictException("Connecting Facebook is not set up on this platform yet.");

        EnsureStateIsOurs(request.State);

        MetaToken token;
        IReadOnlyList<MetaAdAccount> accounts;
        try
        {
            token = await _meta.ExchangeCodeAsync(request.Code, request.RedirectUri, cancellationToken);
            accounts = await _meta.ListAdAccountsAsync(token.AccessToken, cancellationToken);
        }
        catch (MetaAuthException ex)
        {
            throw Invalid("Code", ex.Message);
        }
        catch (MetaApiException ex)
        {
            throw Invalid("Code", ex.Message);
        }

        if (accounts.Count == 0)
            throw Invalid("Code", "That Facebook login has no ad account we can read. Log in with the account that runs your ads.");

        var connection = await _context.SocialAdConnections.FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            connection = new SocialAdConnection { TenantId = TenantId };
            _context.SocialAdConnections.Add(connection);
        }

        connection.AccessToken = _protector.Protect(token.AccessToken);
        connection.TokenExpiresAt = token.ExpiresAtUtc;
        connection.ConnectedAt = _clock.UtcNow;
        connection.LastSyncError = null;

        // Reconnecting after a lapse keeps the account the tenant already chose, when it is still on offer.
        var keep = connection.AdAccountId is null ? null : accounts.FirstOrDefault(a => a.Id == connection.AdAccountId);
        var chosen = keep ?? (accounts.Count == 1 ? accounts[0] : null);

        if (chosen is not null)
        {
            ApplyAccount(connection, chosen);
        }
        else
        {
            connection.Status = SocialAdConnectionStatus.PendingAccountSelection;
            connection.AdAccountId = null;
            connection.AdAccountName = null;
            connection.CurrencyCode = null;
            connection.CandidateAccountsJson = JsonSerializer.Serialize(accounts, Json);
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (chosen is not null)
            await _sync.SyncTenantAsync(TenantId, backfill: keep is null, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<SocialAdsStatusDto> SelectAccountAsync(SelectSocialAdAccountRequest request, CancellationToken cancellationToken = default)
    {
        await _selectValidator.ValidateAndThrowAsync(request, cancellationToken);

        var connection = await _context.SocialAdConnections.FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(SocialAdConnection), TenantId);

        if (connection.Status != SocialAdConnectionStatus.PendingAccountSelection)
            throw new ConflictException("There is no ad account waiting to be chosen. Connect Facebook again to switch accounts.");

        var chosen = ParseCandidates(connection).FirstOrDefault(a => a.Id == request.AdAccountId)
            ?? throw Invalid(nameof(request.AdAccountId), "That ad account is not one of the accounts your Facebook login can see.");

        ApplyAccount(connection, chosen);
        await _context.SaveChangesAsync(cancellationToken);
        await _sync.SyncTenantAsync(TenantId, backfill: true, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<SocialAdsStatusDto> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.SocialAdConnections.FirstOrDefaultAsync(cancellationToken);
        if (connection is null || connection.Status != SocialAdConnectionStatus.Connected)
            throw new ConflictException("Connect Facebook before syncing ad spend.");

        if (connection.LastSyncedAt is { } last && _clock.UtcNow - last < SyncCooldown)
            return ToStatus(connection);

        await _sync.SyncTenantAsync(TenantId, backfill: false, cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.SocialAdConnections.FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
            return;

        var metaRows = await _context.SocialAdSpends.Where(s => s.Source == SocialAdSpend.SourceMeta).ToListAsync(cancellationToken);
        _context.SocialAdSpends.RemoveRange(metaRows);
        _context.SocialAdConnections.Remove(connection);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ManualAdSpendDto>> GetManualSpendAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.SocialAdSpends
            .Where(s => s.Source == SocialAdSpend.SourceManual)
            .OrderByDescending(s => s.Date)
            .Take(36)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ManualAdSpendDto(r.Date, r.Spend, r.CurrencyCode)).ToList();
    }

    public async Task<ManualAdSpendDto?> SaveManualSpendAsync(SaveManualAdSpendRequest request, CancellationToken cancellationToken = default)
    {
        await _manualValidator.ValidateAndThrowAsync(request, cancellationToken);

        var month = new DateTime(request.Month.Year, request.Month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (month > _clock.UtcNow.AddMonths(1))
            throw Invalid(nameof(request.Month), "Spend cannot be recorded for a future month.");

        var row = await _context.SocialAdSpends
            .FirstOrDefaultAsync(s => s.Source == SocialAdSpend.SourceManual && s.Date == month, cancellationToken);

        if (request.Amount == 0)
        {
            if (row is not null)
            {
                _context.SocialAdSpends.Remove(row);
                await _context.SaveChangesAsync(cancellationToken);
            }

            return null;
        }

        if (row is null)
        {
            row = new SocialAdSpend
            {
                TenantId = TenantId,
                Source = SocialAdSpend.SourceManual,
                AdAccountId = string.Empty,
                Date = month,
                Platform = SocialAdSpend.ManualPlatform,
            };
            _context.SocialAdSpends.Add(row);
        }

        row.Spend = request.Amount;
        row.CurrencyCode = await TenantCurrencyAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new ManualAdSpendDto(row.Date, row.Spend, row.CurrencyCode);
    }

    private async Task<string> TenantCurrencyAsync(CancellationToken cancellationToken)
    {
        var country = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == TenantId)
            .Select(t => t.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);
        return RegionalPricingCatalog.Resolve(country).CurrencyCode;
    }

    private SocialAdsStatusDto ToStatus(SocialAdConnection? connection)
    {
        if (connection is null)
            return new SocialAdsStatusDto(_meta.IsConfigured, "NotConnected", null, null, null, null, null, null, null, Array.Empty<SocialAdAccountDto>());

        var accounts = connection.Status == SocialAdConnectionStatus.PendingAccountSelection
            ? ParseCandidates(connection).Select(a => new SocialAdAccountDto(a.Id, a.Name, a.CurrencyCode)).ToList()
            : new List<SocialAdAccountDto>();

        return new SocialAdsStatusDto(
            _meta.IsConfigured,
            connection.Status.ToString(),
            connection.AdAccountId,
            connection.AdAccountName,
            connection.CurrencyCode,
            connection.ConnectedAt,
            connection.LastSyncedAt,
            connection.LastSyncError,
            connection.TokenExpiresAt,
            accounts);
    }

    private static void ApplyAccount(SocialAdConnection connection, MetaAdAccount account)
    {
        connection.Status = SocialAdConnectionStatus.Connected;
        connection.AdAccountId = account.Id;
        connection.AdAccountName = account.Name;
        connection.CurrencyCode = account.CurrencyCode;
        connection.CandidateAccountsJson = null;
    }

    private static List<MetaAdAccount> ParseCandidates(SocialAdConnection connection)
    {
        if (string.IsNullOrEmpty(connection.CandidateAccountsJson))
            return new List<MetaAdAccount>();
        try
        {
            return JsonSerializer.Deserialize<List<MetaAdAccount>>(connection.CandidateAccountsJson, Json) ?? new List<MetaAdAccount>();
        }
        catch (JsonException)
        {
            return new List<MetaAdAccount>();
        }
    }

    /// <summary>The state must be one this server issued, for this tenant, and not expired - otherwise a login started by
    /// someone else (or an old link) could attach their Facebook account to this tenant.</summary>
    private void EnsureStateIsOurs(string state)
    {
        var plain = _protector.TryUnprotect(state);
        var parts = plain?.Split('|');
        if (parts is not { Length: 2 }
            || !string.Equals(parts[0], TenantId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < _clock.UtcNow.Ticks)
        {
            throw Invalid("State", "This Facebook login has expired or did not start here. Please click Connect again.");
        }
    }

    private static void EnsureValidRedirect(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw Invalid(nameof(redirectUri), "The redirect address must be an https address.");
    }

    private static ValidationException Invalid(string field, string message) =>
        new(new[] { new ValidationFailure(field, message) });
}
