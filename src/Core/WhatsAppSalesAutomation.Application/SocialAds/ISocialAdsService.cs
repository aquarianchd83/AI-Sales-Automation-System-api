namespace WhatsAppSalesAutomation.Application.SocialAds;

/// <summary>The tenant's own Meta ad connection: log in once, pick the ad account, and the spend history syncs itself.</summary>
public interface ISocialAdsService
{
    Task<SocialAdsStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The Facebook login address for the signed-in tenant. The "state" in it is signed and expires in 15 minutes.</summary>
    Task<SocialAdsConnectUrlDto> GetConnectUrlAsync(string redirectUri, CancellationToken cancellationToken = default);

    /// <summary>Finishes the login. With exactly one ad account it is connected and its history pulled straight away;
    /// with several, the tenant is asked to choose (status PendingAccountSelection).</summary>
    Task<SocialAdsStatusDto> CompleteConnectAsync(CompleteSocialAdsConnectRequest request, CancellationToken cancellationToken = default);

    Task<SocialAdsStatusDto> SelectAccountAsync(SelectSocialAdAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pulls the latest spend now. A request right after another is answered without calling Meta again.</summary>
    Task<SocialAdsStatusDto> SyncNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the connection, its stored token and every spend row that came from Meta. Typed-in months stay.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManualAdSpendDto>> GetManualSpendAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets one month's typed-in spend; an amount of 0 clears it.</summary>
    Task<ManualAdSpendDto?> SaveManualSpendAsync(SaveManualAdSpendRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Keeps the stored spend in step with Meta. Works on an explicit tenant, so it is correct from any scope.</summary>
public interface ISocialAdSyncService
{
    /// <summary>Pulls spend for one tenant. <paramref name="backfill"/> reaches back 24 months (the first sync after
    /// connecting); otherwise the last 35 days, which also picks up Meta's late corrections. Never throws for a Meta
    /// failure - it is recorded on the connection - so a bad token cannot break a caller.</summary>
    Task SyncTenantAsync(Guid tenantId, bool backfill, CancellationToken cancellationToken = default);

    /// <summary>The daily job: every connected tenant, one after another; a failure for one never stops the rest.</summary>
    Task<int> SyncAllAsync(CancellationToken cancellationToken = default);
}
