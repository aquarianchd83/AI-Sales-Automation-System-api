using System.Text.Json;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.SocialAds;
using WhatsAppSalesAutomation.Domain.Entities.Packages;
using WhatsAppSalesAutomation.Domain.Entities.SocialAds;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.SocialAds;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Connecting Facebook, syncing ad spend, the Meta response parsing, and the social-vs-WhatsApp comparison.</summary>
public sealed class SocialAdsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestClock _clock = new() { UtcNow = Now };
    private readonly FakeMeta _meta = new();
    private readonly FakeProtector _protector = new();
    private readonly SocialAdSyncService _sync;
    private readonly SocialAdsService _service;

    public SocialAdsTests()
    {
        _connection.Open();
        _db = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        _sync = new SocialAdSyncService(_db, _meta, _protector, _clock, NullLogger<SocialAdSyncService>.Instance);
        _service = new SocialAdsService(
            _db, new Ambient(_tenant), _meta, _protector, _sync, _clock,
            new CompleteSocialAdsConnectRequestValidator(), new SelectSocialAdAccountRequestValidator(), new SaveManualAdSpendRequestValidator());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string Redirect = "https://app.example.com/settings/social-ads";

    private async Task<string> StateAsync()
    {
        var url = (await _service.GetConnectUrlAsync(Redirect)).Url;
        return Uri.UnescapeDataString(url[(url.IndexOf("state=", StringComparison.Ordinal) + 6)..].Split('&')[0]);
    }

    private async Task<SocialAdsStatusDto> ConnectAsync(params MetaAdAccount[] accounts)
    {
        _meta.Accounts = accounts;
        return await _service.CompleteConnectAsync(new CompleteSocialAdsConnectRequest("code-1", await StateAsync(), Redirect));
    }

    // ── Connecting ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_ad_account_is_connected_straight_away_and_its_history_is_pulled()
    {
        _meta.Rows = new[] { new MetaInsightRow(new DateTime(2026, 9, 1), "facebook", 120m, 5000, 80, 4) };

        var status = await ConnectAsync(new MetaAdAccount("111", "Acme Ads", "USD"));

        Assert.Equal("Connected", status.Status);
        Assert.Equal("Acme Ads", status.AdAccountName);
        Assert.NotNull(status.LastSyncedAt);
        Assert.Single(_db.SocialAdSpends);
        Assert.Equal(DateTime.Parse("2024-10-01"), _meta.LastSince);   // a 24-month backfill, starting at the month start
    }

    [Fact]
    public async Task Several_ad_accounts_wait_for_the_tenant_to_choose_one()
    {
        var status = await ConnectAsync(new MetaAdAccount("1", "A", "USD"), new MetaAdAccount("2", "B", "USD"));

        Assert.Equal("PendingAccountSelection", status.Status);
        Assert.Equal(2, status.Accounts.Count);
        Assert.Empty(_db.SocialAdSpends);

        var chosen = await _service.SelectAccountAsync(new SelectSocialAdAccountRequest("2"));
        Assert.Equal("Connected", chosen.Status);
        Assert.Equal("B", chosen.AdAccountName);
        Assert.Empty(chosen.Accounts);
    }

    [Fact]
    public async Task An_account_the_login_cannot_see_is_refused()
    {
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"), new MetaAdAccount("2", "B", "USD"));

        await Assert.ThrowsAsync<ValidationException>(() => _service.SelectAccountAsync(new SelectSocialAdAccountRequest("999")));
    }

    [Fact]
    public async Task A_login_state_from_another_tenant_or_made_up_is_refused()
    {
        _meta.Accounts = new[] { new MetaAdAccount("1", "A", "USD") };

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CompleteConnectAsync(new CompleteSocialAdsConnectRequest("c", "not-a-real-state", Redirect)));

        var other = _protector.Protect($"{Guid.NewGuid():N}|{(Now.AddMinutes(5)).Ticks}");
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CompleteConnectAsync(new CompleteSocialAdsConnectRequest("c", other, Redirect)));
    }

    [Fact]
    public async Task An_expired_login_state_is_refused()
    {
        var state = await StateAsync();
        _clock.UtcNow = Now.AddMinutes(16);
        _meta.Accounts = new[] { new MetaAdAccount("1", "A", "USD") };

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CompleteConnectAsync(new CompleteSocialAdsConnectRequest("c", state, Redirect)));
    }

    [Fact]
    public async Task The_token_is_stored_protected_and_never_in_the_status()
    {
        var status = await ConnectAsync(new MetaAdAccount("1", "A", "USD"));

        var stored = await _db.SocialAdConnections.SingleAsync();
        Assert.NotEqual("token-plain", stored.AccessToken);
        Assert.Equal("token-plain", _protector.TryUnprotect(stored.AccessToken));
        Assert.DoesNotContain("token-plain", JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Connecting_is_refused_until_the_platform_has_a_meta_app()
    {
        _meta.Configured = false;

        await Assert.ThrowsAsync<ConflictException>(() => _service.GetConnectUrlAsync(Redirect));
    }

    [Fact]
    public async Task Disconnecting_removes_the_connection_and_meta_spend_but_keeps_typed_in_months()
    {
        _meta.Rows = new[] { new MetaInsightRow(new DateTime(2026, 9, 1), "facebook", 120m, 1, 1, 0) };
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));
        await _service.SaveManualSpendAsync(new SaveManualAdSpendRequest(new DateTime(2026, 8, 15), 900m));

        await _service.DisconnectAsync();

        Assert.Equal("NotConnected", (await _service.GetStatusAsync()).Status);
        Assert.Equal(new[] { SocialAdSpend.SourceManual }, _db.SocialAdSpends.Select(s => s.Source).ToList());
    }

    // ── Syncing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_resync_overwrites_a_day_instead_of_adding_a_second_row_and_drops_a_vanished_day()
    {
        _meta.Rows = new[]
        {
            new MetaInsightRow(new DateTime(2026, 9, 1), "facebook", 100m, 10, 1, 0),
            new MetaInsightRow(new DateTime(2026, 9, 2), "instagram", 50m, 10, 1, 0),
        };
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));

        _meta.Rows = new[] { new MetaInsightRow(new DateTime(2026, 9, 1), "facebook", 130m, 20, 2, 1) };
        await _sync.SyncTenantAsync(_tenant, backfill: false);

        var rows = await _db.SocialAdSpends.ToListAsync();
        Assert.Single(rows);
        Assert.Equal(130m, rows[0].Spend);
        Assert.Equal(1, rows[0].Leads);
    }

    [Fact]
    public async Task A_rejected_token_marks_the_connection_for_reconnect_and_keeps_the_spend_already_pulled()
    {
        _meta.Rows = new[] { new MetaInsightRow(new DateTime(2026, 9, 1), "facebook", 100m, 10, 1, 0) };
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));

        _meta.Failure = new MetaAuthException("Meta no longer accepts the Facebook login. Please connect again.");
        await _sync.SyncTenantAsync(_tenant, backfill: false);

        var status = await _service.GetStatusAsync();
        Assert.Equal("NeedsReconnect", status.Status);
        Assert.Contains("connect again", status.LastSyncError);
        Assert.Single(_db.SocialAdSpends);
    }

    [Fact]
    public async Task A_temporary_meta_error_keeps_the_connection_and_records_why()
    {
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));

        _meta.Failure = new MetaApiException("Meta rejected the request (500).");
        await _sync.SyncTenantAsync(_tenant, backfill: false);

        var status = await _service.GetStatusAsync();
        Assert.Equal("Connected", status.Status);
        Assert.Equal("Meta rejected the request (500).", status.LastSyncError);
    }

    [Fact]
    public async Task A_token_close_to_expiry_is_renewed_during_the_sync()
    {
        _meta.TokenExpiry = Now.AddDays(5);
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));
        _meta.RefreshCalls = 0;
        _meta.TokenExpiry = Now.AddDays(60);

        await _sync.SyncTenantAsync(_tenant, backfill: false);

        Assert.Equal(1, _meta.RefreshCalls);
        Assert.Equal(Now.AddDays(60), (await _db.SocialAdConnections.SingleAsync()).TokenExpiresAt);
    }

    [Fact]
    public async Task An_expired_token_is_not_sent_to_meta_at_all()
    {
        _meta.TokenExpiry = Now.AddDays(1);
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));
        _meta.InsightCalls = 0;
        _clock.UtcNow = Now.AddDays(2);

        await _sync.SyncTenantAsync(_tenant, backfill: false);

        Assert.Equal(0, _meta.InsightCalls);
        Assert.Equal("NeedsReconnect", (await _service.GetStatusAsync()).Status);
    }

    [Fact]
    public async Task Sync_now_right_after_a_sync_does_not_call_meta_again()
    {
        await ConnectAsync(new MetaAdAccount("1", "A", "USD"));
        _meta.InsightCalls = 0;

        await _service.SyncNowAsync();

        Assert.Equal(0, _meta.InsightCalls);
    }

    [Fact]
    public void Long_ranges_are_split_into_requests_of_at_most_three_months_covering_every_day()
    {
        var chunks = SocialAdSyncService.Chunks(new DateTime(2024, 10, 1), new DateTime(2026, 9, 21)).ToList();

        Assert.Equal(new DateTime(2024, 10, 1), chunks[0].From);
        Assert.Equal(new DateTime(2024, 12, 31), chunks[0].To);
        Assert.Equal(new DateTime(2026, 9, 21), chunks[^1].To);
        for (var i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].To.AddDays(1), chunks[i].From);
    }

    // ── Manual spend ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Typed_in_spend_is_saved_per_month_replaced_and_cleared_with_zero()
    {
        await _service.SaveManualSpendAsync(new SaveManualAdSpendRequest(new DateTime(2026, 8, 20), 900m));
        await _service.SaveManualSpendAsync(new SaveManualAdSpendRequest(new DateTime(2026, 8, 3), 1100m));

        var saved = await _service.GetManualSpendAsync();
        Assert.Single(saved);
        Assert.Equal(new DateTime(2026, 8, 1), saved[0].Month);
        Assert.Equal(1100m, saved[0].Amount);

        await _service.SaveManualSpendAsync(new SaveManualAdSpendRequest(new DateTime(2026, 8, 3), 0m));
        Assert.Empty(await _service.GetManualSpendAsync());
    }

    // ── Comparison ───────────────────────────────────────────────────────────────────────

    private MarketingComparisonService Comparison(decimal whatsAppUsd = 0m) =>
        new(_db, new Ambient(_tenant), new FakeWhatsAppSpend(whatsAppUsd), _clock);

    private void Sell(int count, decimal amount)
    {
        var package = new SalesPackage { TenantId = _tenant, Name = "Gold", Price = amount };
        _db.SalesPackages.Add(package);
        _db.SaveChanges();
        for (var i = 0; i < count; i++)
            _db.PackageSales.Add(new PackageSale { TenantId = _tenant, PackageId = package.Id, Amount = amount, SoldAt = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc) });
        _db.SaveChanges();
    }

    private void Spend(string source, DateTime date, string platform, decimal spend, long clicks = 0, int leads = 0, string currency = "USD") =>
        _db.SocialAdSpends.Add(new SocialAdSpend
        {
            TenantId = _tenant, Source = source, AdAccountId = source == SocialAdSpend.SourceMeta ? "1" : string.Empty,
            Date = date, Platform = platform, Spend = spend, Clicks = clicks, Leads = leads, CurrencyCode = currency,
        });

    [Fact]
    public async Task Cost_per_sale_and_return_on_spend_are_worked_out_for_both_channels_and_the_cheaper_one_named()
    {
        Sell(count: 4, amount: 1000m);                                    // revenue 4000
        Spend(SocialAdSpend.SourceMeta, new DateTime(2026, 9, 2), "facebook", 800m, clicks: 400, leads: 20);
        Spend(SocialAdSpend.SourceMeta, new DateTime(2026, 9, 3), "instagram", 200m, clicks: 100, leads: 5);
        _db.SaveChanges();

        var result = await Comparison(whatsAppUsd: 100m).GetAsync(3);

        Assert.Equal("Meta", result.SpendSource);
        Assert.Equal(1000m, result.Social.Spend);
        Assert.Equal(250m, result.Social.CostPerSale);       // 1000 / 4
        Assert.Equal(2m, result.Social.CostPerClick);        // 1000 / 500
        Assert.Equal(40m, result.Social.CostPerLead);        // 1000 / 25
        Assert.Equal(4.0, result.Social.ReturnOnSpend);
        Assert.Equal(100m, result.WhatsApp.Cost);
        Assert.Equal(25m, result.WhatsApp.CostPerSale);
        Assert.Equal("WhatsApp", result.CheaperChannel);
        Assert.Equal(225m, result.SavingsPerSale);
        Assert.Equal(new[] { "facebook", "instagram" }, result.Platforms.Select(p => p.Platform));
        Assert.Equal(80.0, result.Platforms[0].SharePercent);
    }

    [Fact]
    public async Task Real_ad_data_wins_over_typed_in_months_so_nothing_is_counted_twice()
    {
        Spend(SocialAdSpend.SourceMeta, new DateTime(2026, 9, 2), "facebook", 500m);
        Spend(SocialAdSpend.SourceManual, new DateTime(2026, 9, 1), SocialAdSpend.ManualPlatform, 9999m);
        _db.SaveChanges();

        var result = await Comparison().GetAsync(3);

        Assert.Equal(500m, result.Social.Spend);
    }

    [Fact]
    public async Task Typed_in_months_are_used_when_there_is_no_ad_account_data()
    {
        Spend(SocialAdSpend.SourceManual, new DateTime(2026, 9, 1), SocialAdSpend.ManualPlatform, 700m);
        _db.SaveChanges();

        var result = await Comparison().GetAsync(3);

        Assert.Equal("Manual", result.SpendSource);
        Assert.Equal(700m, result.Social.Spend);
    }

    [Fact]
    public async Task Ad_spend_in_a_different_currency_gives_no_cross_currency_figures()
    {
        Sell(count: 2, amount: 1000m);
        Spend(SocialAdSpend.SourceMeta, new DateTime(2026, 9, 2), "facebook", 500m, clicks: 50, currency: "EUR");
        _db.SaveChanges();

        var result = await Comparison().GetAsync(3);

        Assert.False(result.CurrencyMatches);
        Assert.Equal("EUR", result.SpendCurrencyCode);
        Assert.Null(result.Social.CostPerSale);
        Assert.Null(result.Social.ReturnOnSpend);
        Assert.Null(result.CheaperChannel);
        Assert.Equal(10m, result.Social.CostPerClick);      // a per-click figure needs no conversion
    }

    [Fact]
    public async Task With_no_spend_and_no_sales_nothing_divides_by_zero()
    {
        var result = await Comparison().GetAsync(3);

        Assert.Equal("None", result.SpendSource);
        Assert.Null(result.Social.CostPerSale);
        Assert.Null(result.Social.CostPerClick);
        Assert.Null(result.Social.ReturnOnSpend);
        Assert.Null(result.WhatsApp.CostPerSale);
        Assert.Equal(3, result.SpendTrend.Count);
    }

    // ── Meta responses ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Insight_rows_are_read_with_leads_summed_across_the_lead_action_types()
    {
        using var doc = JsonDocument.Parse("""
        {"data":[
          {"date_start":"2026-09-01","publisher_platform":"Instagram","spend":"12.50","impressions":"1000","clicks":"40",
           "actions":[{"action_type":"lead","value":"3"},{"action_type":"offsite_conversion.fb_pixel_lead","value":"2"},{"action_type":"link_click","value":"40"}]},
          {"date_start":"not-a-date","spend":"1"},
          {"date_start":"2026-09-02","spend":"4","impressions":"10","clicks":"1"}
        ]}
        """);

        var rows = MetaAdsClient.ParseInsights(doc.RootElement);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new MetaInsightRow(new DateTime(2026, 9, 1), "instagram", 12.5m, 1000, 40, 5), rows[0]);
        Assert.Equal("unknown", rows[1].Platform);
        Assert.Equal(0, rows[1].Leads);
    }

    [Theory]
    [InlineData(400, """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}""", typeof(MetaAuthException))]
    [InlineData(400, """{"error":{"message":"Session has expired","type":"OAuthException","code":102}}""", typeof(MetaAuthException))]
    [InlineData(400, """{"error":{"message":"Rate limit","type":"OAuthException","code":17}}""", typeof(MetaApiException))]
    [InlineData(500, "<html>oops</html>", typeof(MetaApiException))]
    public void Only_an_invalid_token_asks_the_tenant_to_reconnect(int status, string body, Type expected)
    {
        Assert.IsType(expected, MetaAdsClient.ToException(status, body));
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────────

    private sealed class FakeMeta : IMetaAdsClient
    {
        public bool Configured { get; set; } = true;
        public IReadOnlyList<MetaAdAccount> Accounts { get; set; } = Array.Empty<MetaAdAccount>();
        public IReadOnlyList<MetaInsightRow> Rows { get; set; } = Array.Empty<MetaInsightRow>();
        public Exception? Failure { get; set; }
        public DateTime? TokenExpiry { get; set; } = Now.AddDays(60);
        public int RefreshCalls { get; set; }
        public int InsightCalls { get; set; }
        public DateTime? LastSince { get; private set; }

        public bool IsConfigured => Configured;

        public string BuildLoginUrl(string redirectUri, string state) =>
            $"https://www.facebook.com/dialog/oauth?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

        public Task<MetaToken> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetaToken("token-plain", TokenExpiry));

        public Task<MetaToken> RefreshTokenAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(new MetaToken("token-renewed", TokenExpiry));
        }

        public Task<IReadOnlyList<MetaAdAccount>> ListAdAccountsAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts);

        public Task<IReadOnlyList<MetaInsightRow>> GetInsightsAsync(
            string accessToken, string adAccountId, DateTime since, DateTime until, CancellationToken cancellationToken = default)
        {
            InsightCalls++;
            LastSince ??= since;
            if (Failure is not null)
                throw Failure;
            // Only the rows inside the requested chunk, as Meta would return.
            return Task.FromResult<IReadOnlyList<MetaInsightRow>>(Rows.Where(r => r.Date >= since && r.Date <= until).ToList());
        }
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plainText) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

        public string? TryUnprotect(string protectedValue)
        {
            if (!protectedValue.StartsWith("enc:", StringComparison.Ordinal))
                return null;
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..]));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    private sealed class FakeWhatsAppSpend : IWhatsAppSpendService
    {
        private readonly decimal _usd;

        public FakeWhatsAppSpend(decimal usd) => _usd = usd;

        public Task<WhatsAppSpend> GetForTenantAsync(Guid tenantId, DateTime fromUtc, DateTime? toUtc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WhatsAppSpend(10, 10, 0, _usd, Array.Empty<WhatsAppCategorySpend>()));

        public Task<IReadOnlyDictionary<Guid, WhatsAppSpend>> GetForTenantsAsync(
            IReadOnlyCollection<Guid> tenantIds, DateTime fromUtc, DateTime? toUtc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, WhatsAppSpend>>(new Dictionary<Guid, WhatsAppSpend>());
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
