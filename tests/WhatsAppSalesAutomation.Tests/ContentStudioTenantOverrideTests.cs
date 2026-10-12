using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Settings;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The AI Content Studio settings live with the rest of a tenant's configuration: each is a platform default that the
/// tenant's Advanced settings can override, so the studio can be on for one tenant and off for the rest.</summary>
public sealed class ContentStudioTenantOverrideTests : IDisposable
{
    private sealed class Snapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public Snapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantConfigOverrideProvider _provider;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    public ContentStudioTenantOverrideTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();

        _provider = new TenantConfigOverrideProvider(
            _db, new FakeTenantContext(), new FakeClock(), new UpdateTenantSettingsRequestValidator(),
            new Snapshot<CampaignOptions>(new CampaignOptions()),
            new Snapshot<MediaOptions>(new MediaOptions()),
            new Snapshot<MessagingOptions>(new MessagingOptions()),
            new Snapshot<AiOptions>(new AiOptions()),
            new Snapshot<StudioOptions>(new StudioOptions { Enabled = false }));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task SaveAsync(Guid tenant, params (string Key, string? Value)[] values) =>
        _provider.SaveOverridesForTenantAsync(tenant, new UpdateTenantSettingsRequest(values.ToDictionary(v => v.Key, v => v.Value)), null);

    [Fact]
    public async Task A_tenant_with_no_override_runs_on_the_platform_default()
    {
        var options = await _provider.GetStudioOptionsForTenantAsync(_tenant);

        Assert.False(options.Enabled);
        Assert.Equal("PublicBucket", options.StorageMode);
        Assert.Equal("studio", options.StorageKeyPrefix);
    }

    [Fact]
    public async Task The_studio_can_be_switched_on_for_one_tenant_only()
    {
        await SaveAsync(_tenant, ("ContentStudio:Enabled", "true"));

        Assert.True((await _provider.GetStudioOptionsForTenantAsync(_tenant)).Enabled);
        Assert.False((await _provider.GetStudioOptionsForTenantAsync(_other)).Enabled);
    }

    [Fact]
    public async Task An_override_changes_only_its_own_setting_and_a_blank_returns_to_the_default()
    {
        await SaveAsync(_tenant, ("ContentStudio:StorageMode", "SignedUrls"), ("ContentStudio:SignedUrlMinutes", "15"));

        var overridden = await _provider.GetStudioOptionsForTenantAsync(_tenant);
        Assert.Equal("SignedUrls", overridden.StorageMode);
        Assert.Equal(15, overridden.SignedUrlMinutes);
        Assert.Equal("studio", overridden.StorageKeyPrefix);

        await SaveAsync(_tenant, ("ContentStudio:StorageMode", ""));
        Assert.Equal("PublicBucket", (await _provider.GetStudioOptionsForTenantAsync(_tenant)).StorageMode);
    }

    [Fact]
    public async Task The_tenants_advanced_settings_list_the_studio_settings_with_their_platform_defaults()
    {
        await SaveAsync(_tenant, ("ContentStudio:Enabled", "true"));

        var category = (await _provider.GetOverridesForTenantAsync(_tenant)).Single(c => c.Category == "ContentStudio");

        Assert.Equal(4, category.Items.Count);
        var enabled = category.Items.Single(i => i.Key == "ContentStudio:Enabled");
        Assert.Equal("false", enabled.GlobalValue);
        Assert.Equal("true", enabled.OverrideValue);
        Assert.Equal("true", enabled.EffectiveValue);
        Assert.Null(category.Items.Single(i => i.Key == "ContentStudio:StorageMode").OverrideValue);
    }

    [Fact]
    public async Task Every_overridable_setting_in_the_catalog_can_be_listed_for_a_tenant()
    {
        // The Advanced settings list is built from the catalog; a key marked overridable but not wired up used to crash it for every tenant.
        var listed = (await _provider.GetOverridesForTenantAsync(_tenant)).SelectMany(c => c.Items).Select(i => i.Key).ToHashSet();
        var overridable = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All.Where(d => d.IsTenantOverridable).Select(d => d.Key);

        Assert.All(overridable, key => Assert.Contains(key, listed));
    }

    [Fact]
    public async Task The_ai_settings_that_were_missing_from_the_list_take_an_override()
    {
        await SaveAsync(_tenant, ("Ai:MinFieldExtractionConfidence", "0.8"), ("Ai:MaxFieldsToAsk", "5"), ("Ai:HandoffOnHotLead", "false"));

        var ai = (await _provider.GetOverridesForTenantAsync(_tenant)).Single(c => c.Category == "Ai").Items;
        Assert.Equal("0.8", ai.Single(i => i.Key == "Ai:MinFieldExtractionConfidence").EffectiveValue);
        Assert.Equal("5", ai.Single(i => i.Key == "Ai:MaxFieldsToAsk").EffectiveValue);
        Assert.Equal("false", ai.Single(i => i.Key == "Ai:HandoffOnHotLead").EffectiveValue);
    }

    [Theory]
    [InlineData("ContentStudio:Enabled", "yes")]
    [InlineData("ContentStudio:StorageMode", "Dropbox")]
    [InlineData("ContentStudio:StorageKeyPrefix", "../etc")]
    [InlineData("ContentStudio:SignedUrlMinutes", "0")]
    [InlineData("ContentStudio:SignedUrlMinutes", "soon")]
    public async Task A_value_that_makes_no_sense_for_the_setting_is_refused_and_nothing_is_stored(string key, string value)
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => SaveAsync(_tenant, (key, value)));

        Assert.Equal(0, await _db.TenantAppSettingOverrides.IgnoreQueryFilters().CountAsync());
    }

    [Theory]
    [InlineData("ContentStudio:Enabled", "TRUE")]
    [InlineData("ContentStudio:StorageMode", "signedurls")]
    [InlineData("ContentStudio:StorageKeyPrefix", "studio-videos_2")]
    [InlineData("ContentStudio:SignedUrlMinutes", "1440")]
    public async Task A_sensible_value_is_accepted(string key, string value)
    {
        await SaveAsync(_tenant, (key, value));

        Assert.Equal(1, await _db.TenantAppSettingOverrides.IgnoreQueryFilters().CountAsync());
    }
}
