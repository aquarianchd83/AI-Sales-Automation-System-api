using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

/// <summary>
/// The one place a saved sign-in is turned into an access token that works now: the saved token while it has time left, otherwise a renewed one
/// (stored encrypted, refresh token kept unless Google rotates it). A sign-in that can no longer be read or renewed ends as "connect again".
/// Used by the sender and by the daily metrics sync, so both behave the same.
/// </summary>
public static class SocialTokens
{
    public static async Task<string> FreshAccessTokenAsync(
        IApplicationDbContext db, ISecretProtector protector, IYouTubeClient youtube, IDateTimeProvider clock, SocialConnection connection, CancellationToken ct)
    {
        var access = protector.TryUnprotect(connection.EncryptedAccessToken);
        if (access is not null && connection.TokenExpiresAtUtc > clock.UtcNow + PublishingLimits.RefreshBefore)
            return access;

        var refresh = protector.TryUnprotect(connection.EncryptedRefreshToken)
            ?? throw new SocialProviderException("The saved sign-in can no longer be read. Connect your YouTube account again.", SocialFailure.NeedsReconnect);
        var tokens = await youtube.RefreshAsync(refresh, ct);
        connection.EncryptedAccessToken = protector.Protect(tokens.AccessToken);
        connection.TokenExpiresAtUtc = tokens.ExpiresAtUtc;
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            connection.EncryptedRefreshToken = protector.Protect(tokens.RefreshToken);
        connection.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return tokens.AccessToken;
    }
}
