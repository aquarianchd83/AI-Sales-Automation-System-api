namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Reads/writes the single persisted WhatsAppAccessTokenState row - see that entity's own
/// doc comment for why this is a separate Infrastructure-only store rather than something reached
/// through IApplicationDbContext.</summary>
public interface IWhatsAppTokenStore
{
    /// <summary>The token/expiry to actually use for the next Cloud API call - the DB-persisted one
    /// if WhatsAppTokenRefreshService has ever run successfully, otherwise falls back to
    /// WhatsAppSettings.AccessToken/null (the bootstrap value from config, whose real expiry is
    /// unknown until the first refresh establishes it).</summary>
    Task<WhatsAppTokenSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task SaveRefreshedTokenAsync(string accessToken, DateTime? expiresAt, CancellationToken cancellationToken = default);
}

public record WhatsAppTokenSnapshot(string AccessToken, DateTime? ExpiresAt);
