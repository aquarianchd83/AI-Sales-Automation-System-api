using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The tenant's choice between "Automatic" (Connect with Meta) and "Manual" (details typed in): kept on the tenant's own
/// connection row, validated, isolated per tenant, and never touching the credentials it sits beside.</summary>
public sealed class TenantWhatsAppConnectionModeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly TenantWhatsAppConfigProvider _provider;

    public TenantWhatsAppConnectionModeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _provider = new TenantWhatsAppConfigProvider(_db, new Ambient(_tenant), new EphemeralDataProtectionProvider(), new TestClock(), new UpdateTenantWhatsAppConfigRequestValidator());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<TenantWhatsAppConfigDto> SaveAsync(Guid tenant, string phoneId, string? mode = null, string? token = "EAAG-token") =>
        _provider.SaveConfigForTenantAsync(
            tenant, new UpdateTenantWhatsAppConfigRequest(phoneId, "2233445", token, "app-secret", "v19.0", "https://graph.facebook.com/", ConnectionMode: mode), null);

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    [Fact]
    public async Task A_connection_saved_before_modes_existed_has_no_mode_and_still_works()
    {
        var saved = await SaveAsync(_tenant, "1098765");

        Assert.Null(saved.ConnectionMode);
        Assert.True(saved.IsConnected);
    }

    [Theory]
    [InlineData("Automatic")]
    [InlineData("Manual")]
    public async Task Each_mode_is_saved_and_read_back(string mode)
    {
        await SaveAsync(_tenant, "1098765");

        var set = await _provider.SetConnectionModeForTenantAsync(_tenant, mode, null);
        var read = await _provider.GetConfigForTenantAsync(_tenant);

        Assert.Equal(mode, set.ConnectionMode);
        Assert.Equal(mode, read!.ConnectionMode);
    }

    [Fact]
    public async Task Switching_from_one_mode_to_the_other_replaces_it()
    {
        await SaveAsync(_tenant, "1098765");

        await _provider.SetConnectionModeForTenantAsync(_tenant, WhatsAppConnectionModes.Automatic, null);
        await _provider.SetConnectionModeForTenantAsync(_tenant, WhatsAppConnectionModes.Manual, null);
        Assert.Equal("Manual", (await _provider.GetConfigForTenantAsync(_tenant))!.ConnectionMode);

        await _provider.SetConnectionModeForTenantAsync(_tenant, WhatsAppConnectionModes.Automatic, null);
        Assert.Equal("Automatic", (await _provider.GetConfigForTenantAsync(_tenant))!.ConnectionMode);
    }

    [Fact]
    public async Task Changing_the_mode_leaves_credentials_and_the_verification_result_alone()
    {
        await SaveAsync(_tenant, "1098765");
        var row = await _db.TenantWhatsAppConfigs.IgnoreQueryFilters().SingleAsync(c => c.TenantId == _tenant);
        row.VerifiedAtUtc = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        row.VerifiedDisplayPhoneNumber = "+91 98765 43210";
        await _db.SaveChangesAsync();
        var tokenBefore = row.AccessToken;
        var secretBefore = row.AppSecret;

        var after = await _provider.SetConnectionModeForTenantAsync(_tenant, WhatsAppConnectionModes.Manual, null);

        Assert.Equal("1098765", after.PhoneNumberId);
        Assert.True(after.IsConnected);
        Assert.NotNull(after.VerifiedAtUtc);
        Assert.Equal("+91 98765 43210", after.VerifiedDisplayPhoneNumber);
        var stored = await _db.TenantWhatsAppConfigs.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.TenantId == _tenant);
        Assert.Equal(tokenBefore, stored.AccessToken);
        Assert.Equal(secretBefore, stored.AppSecret);
    }

    [Fact]
    public async Task One_tenants_mode_never_changes_another_tenants()
    {
        await SaveAsync(_tenant, "1098765", WhatsAppConnectionModes.Automatic);
        await SaveAsync(_other, "5550001", WhatsAppConnectionModes.Automatic);

        await _provider.SetConnectionModeForTenantAsync(_tenant, WhatsAppConnectionModes.Manual, null);

        Assert.Equal("Manual", (await _provider.GetConfigForTenantAsync(_tenant))!.ConnectionMode);
        var other = (await _provider.GetConfigForTenantAsync(_other))!;
        Assert.Equal("Automatic", other.ConnectionMode);
        Assert.Equal("5550001", other.PhoneNumberId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("automatic")]
    [InlineData("Hybrid")]
    [InlineData("Manual ")]
    public async Task An_unsupported_mode_is_refused_and_the_saved_one_is_kept(string mode)
    {
        await SaveAsync(_tenant, "1098765", WhatsAppConnectionModes.Manual);

        await Assert.ThrowsAsync<ValidationException>(() => _provider.SetConnectionModeForTenantAsync(_tenant, mode, null));

        Assert.Equal("Manual", (await _provider.GetConfigForTenantAsync(_tenant))!.ConnectionMode);
    }

    [Fact]
    public async Task An_unsupported_mode_cannot_ride_in_on_a_credentials_save()
    {
        await Assert.ThrowsAsync<ValidationException>(() => SaveAsync(_tenant, "1098765", "Hybrid"));

        Assert.Null(await _provider.GetConfigForTenantAsync(_tenant));
    }

    [Fact]
    public async Task A_credentials_save_without_a_mode_keeps_the_one_already_chosen()
    {
        await SaveAsync(_tenant, "1098765", WhatsAppConnectionModes.Manual);

        var resaved = await _provider.SaveConfigForTenantAsync(
            _tenant, new UpdateTenantWhatsAppConfigRequest("1098765", "2233445", null, null, "v19.0", "https://graph.facebook.com/"), null);

        Assert.Equal("Manual", resaved.ConnectionMode);
    }

    [Fact]
    public async Task With_nothing_connected_yet_there_is_no_row_to_hold_the_mode()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _provider.SetConnectionModeForTenantAsync(_tenant, WhatsAppConnectionModes.Manual, null));

        Assert.Null(await _provider.GetConfigForTenantAsync(_tenant));
    }
}
