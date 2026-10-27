using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>
/// EF Core store for <see cref="TenantWhatsAppChannel"/>. The instance token is encrypted with the same protector as every other secret;
/// the webhook credentials are only ever stored as SHA-256 hashes - they are 256 random bits, so a plain hash is enough and the plaintext
/// exists only in the one return value that hands it to the bridge.
/// </summary>
public class WhatsAppChannelStore : IWhatsAppChannelStore
{
    private readonly ApplicationDbContext _context;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDateTimeProvider _dateTime;

    public WhatsAppChannelStore(ApplicationDbContext context, IDataProtectionProvider dataProtectionProvider, IDateTimeProvider dateTime)
    {
        _context = context;
        _dataProtectionProvider = dataProtectionProvider;
        _dateTime = dateTime;
    }

    public async Task<WhatsAppChannelRecord?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await _context.TenantWhatsAppChannels.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async Task<WhatsAppChannelRecord?> FindByWebhookAsync(string channelToken, string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelToken) || string.IsNullOrWhiteSpace(secret))
            return null;

        var row = await _context.TenantWhatsAppChannels.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.WebhookTokenHash == Hash(channelToken), cancellationToken);

        // Constant-time, and the same "not found" answer whether the token or only the secret was wrong.
        if (row is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.WebhookSecretHash), Encoding.ASCII.GetBytes(Hash(secret))))
            return null;

        return ToRecord(row);
    }

    public async Task<WhatsAppChannelWebhookCredentials> UpsertAsync(
        Guid tenantId, string instanceName, string instanceToken, string status, CancellationToken cancellationToken = default)
    {
        var credentials = new WhatsAppChannelWebhookCredentials(RandomHex(), RandomHex());
        var now = _dateTime.UtcNow;

        var row = await _context.TenantWhatsAppChannels.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            row = new TenantWhatsAppChannel { TenantId = tenantId, CreatedAtUtc = now };
            _context.TenantWhatsAppChannels.Add(row);
        }

        row.InstanceName = instanceName;
        row.InstanceTokenCipher = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider).Protect(instanceToken);
        row.WebhookTokenHash = Hash(credentials.ChannelToken);
        row.WebhookSecretHash = Hash(credentials.Secret);
        row.Status = status;
        row.ConnectedPhone = null;
        row.LastError = null;
        row.StatusUpdatedAtUtc = now;

        await _context.SaveChangesAsync(cancellationToken);
        return credentials;
    }

    public async Task UpdateStatusAsync(Guid tenantId, string status, string? connectedPhone, string? lastError, CancellationToken cancellationToken = default)
    {
        var row = await _context.TenantWhatsAppChannels.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
            return;

        row.Status = status;
        row.ConnectedPhone = connectedPhone;
        row.LastError = lastError is null ? null : lastError.Length > 300 ? lastError[..300] : lastError;
        row.StatusUpdatedAtUtc = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await _context.TenantWhatsAppChannels.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
        if (row is null)
            return;

        _context.TenantWhatsAppChannels.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private WhatsAppChannelRecord ToRecord(TenantWhatsAppChannel row) => new(
        row.TenantId, row.InstanceName, AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider).Unprotect(row.InstanceTokenCipher),
        row.Status, row.ConnectedPhone, row.LastError, row.StatusUpdatedAtUtc, row.CreatedAtUtc);

    private static string RandomHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
