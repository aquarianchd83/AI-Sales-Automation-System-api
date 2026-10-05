namespace WhatsAppSalesAutomation.Application.SocialAds;

/// <summary>A Meta ad account the logged-in Facebook user can read. <paramref name="Id"/> has no "act_" prefix.</summary>
public record MetaAdAccount(string Id, string Name, string CurrencyCode);

/// <summary>A long-lived user token. <paramref name="ExpiresAtUtc"/> is null when Meta reports no expiry.</summary>
public record MetaToken(string AccessToken, DateTime? ExpiresAtUtc);

/// <summary>One day of one platform's results from the Marketing API insights endpoint.</summary>
public record MetaInsightRow(DateTime Date, string Platform, decimal Spend, long Impressions, long Clicks, int Leads);

/// <summary>Meta no longer accepts the token (code 190): expired, revoked, or the user changed their password.</summary>
public class MetaAuthException : Exception
{
    public MetaAuthException(string message) : base(message) { }
}

/// <summary>Any other failure talking to Meta. The message never contains a token, a secret or a URL.</summary>
public class MetaApiException : Exception
{
    public MetaApiException(string message) : base(message) { }
}

/// <summary>
/// The read-only slice of Meta's Graph / Marketing API the Revenue report needs: log a tenant in with Facebook,
/// list its ad accounts, and read daily spend and results. Requests only the <c>ads_read</c> permission.
/// </summary>
public interface IMetaAdsClient
{
    /// <summary>True once a Meta App ID and secret are set - on the Meta Ads settings, or reused from WhatsApp's.</summary>
    bool IsConfigured { get; }

    /// <summary>Where to send the tenant to log in with Facebook and approve ad-spend access.</summary>
    string BuildLoginUrl(string redirectUri, string state);

    /// <summary>Trades the one-time login code for a ~60-day token.</summary>
    Task<MetaToken> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default);

    /// <summary>Trades a token that is nearing expiry for a fresh ~60-day one.</summary>
    Task<MetaToken> RefreshTokenAsync(string accessToken, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetaAdAccount>> ListAdAccountsAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>Daily spend and results by platform (facebook / instagram / ...) for <paramref name="since"/> through
    /// <paramref name="until"/> inclusive. Pages through the whole result.</summary>
    Task<IReadOnlyList<MetaInsightRow>> GetInsightsAsync(
        string accessToken, string adAccountId, DateTime since, DateTime until, CancellationToken cancellationToken = default);
}
