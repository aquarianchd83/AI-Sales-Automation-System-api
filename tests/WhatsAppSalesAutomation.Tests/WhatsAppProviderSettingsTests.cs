using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

public class WhatsAppProviderSettingsTests
{
    private readonly MemoryStore _store = new();
    private readonly FakeBridge _bridge = new();

    private WhatsAppProviderSettingsService Service()
    {
        var bridgeSettings = new WhatsAppBridgeSettingsService(_store, new UpdateWhatsAppBridgeSettingsRequestValidator(), _bridge);
        return new WhatsAppProviderSettingsService(_store, new UpdateWhatsAppProviderRequestValidator(), bridgeSettings, _bridge);
    }

    private void ConfigureBridge()
    {
        _store.Rows[WhatsAppBridgeSettingsService.BaseUrlKey] = "http://bridge";
        _store.Rows[WhatsAppBridgeSettingsService.ApiKeyKey] = "key";
    }

    [Fact]
    public async Task Nothing_stored_means_Meta_is_active()
    {
        Assert.Equal(WhatsAppProviders.Meta, await Service().GetActiveAsync());
        Assert.True((await Service().GetAsync()).Providers.Single(p => p.Id == WhatsAppProviders.Meta).Available);
    }

    [Fact]
    public async Task The_bridge_is_listed_but_not_selectable_until_it_is_configured()
    {
        var bridge = (await Service().GetAsync()).Providers.Single(p => p.Id == WhatsAppProviders.QrBridge);
        Assert.False(bridge.Available);
        Assert.False(string.IsNullOrEmpty(bridge.UnavailableReason));

        ConfigureBridge();
        Assert.True((await Service().GetAsync()).Providers.Single(p => p.Id == WhatsAppProviders.QrBridge).Available);
    }

    [Fact]
    public async Task Choosing_an_unconfigured_bridge_is_refused_and_leaves_the_active_provider_alone()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().UpdateAsync(new UpdateWhatsAppProviderRequest(WhatsAppProviders.QrBridge), Guid.NewGuid()));

        Assert.DoesNotContain(WhatsAppProviderSettingsService.ActiveKey, _store.Rows.Keys);
        Assert.Equal(WhatsAppProviders.Meta, await Service().GetActiveAsync());
    }

    [Fact]
    public async Task An_unreachable_bridge_can_never_become_the_active_provider()
    {
        ConfigureBridge();
        _bridge.PingFails = true;

        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().UpdateAsync(new UpdateWhatsAppProviderRequest(WhatsAppProviders.QrBridge), Guid.NewGuid()));

        Assert.Equal(WhatsAppProviders.Meta, await Service().GetActiveAsync());
    }

    [Fact]
    public async Task A_configured_reachable_bridge_can_be_activated_and_the_previous_provider_is_reported_for_the_audit_log()
    {
        ConfigureBridge();

        var change = await Service().UpdateAsync(new UpdateWhatsAppProviderRequest(WhatsAppProviders.QrBridge), Guid.NewGuid());

        Assert.Equal(WhatsAppProviders.Meta, change.Previous);
        Assert.Equal(WhatsAppProviders.QrBridge, change.Settings.Active);
        Assert.Equal(WhatsAppProviders.QrBridge, await Service().GetActiveAsync());
    }

    [Fact]
    public async Task Switching_back_to_Meta_needs_no_bridge()
    {
        _store.Rows[WhatsAppProviderSettingsService.ActiveKey] = WhatsAppProviders.QrBridge;

        var change = await Service().UpdateAsync(new UpdateWhatsAppProviderRequest(WhatsAppProviders.Meta), Guid.NewGuid());

        Assert.Equal(WhatsAppProviders.QrBridge, change.Previous);
        Assert.Equal(WhatsAppProviders.Meta, await Service().GetActiveAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Twilio")]
    public async Task An_unknown_provider_is_refused(string provider)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().UpdateAsync(new UpdateWhatsAppProviderRequest(provider), Guid.NewGuid()));
    }

    // ---- the bridge settings themselves ---------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_bridge_settings_are_not_saved_and_the_working_ones_are_kept()
    {
        ConfigureBridge();
        _bridge.PingFails = true;
        var settings = new WhatsAppBridgeSettingsService(_store, new UpdateWhatsAppBridgeSettingsRequestValidator(), _bridge);

        await Assert.ThrowsAsync<ValidationException>(() =>
            settings.UpdateAsync(new UpdateWhatsAppBridgeSettingsRequest("http://other", "newkey", "https://api", null), Guid.NewGuid()));

        Assert.Equal("http://bridge", _store.Rows[WhatsAppBridgeSettingsService.BaseUrlKey]);
        Assert.Equal("key", _store.Rows[WhatsAppBridgeSettingsService.ApiKeyKey]);
    }

    [Fact]
    public async Task A_malformed_bridge_address_is_refused()
    {
        var settings = new WhatsAppBridgeSettingsService(_store, new UpdateWhatsAppBridgeSettingsRequestValidator(), _bridge);

        await Assert.ThrowsAsync<ValidationException>(() =>
            settings.UpdateAsync(new UpdateWhatsAppBridgeSettingsRequest("not a url", "k", "https://api", null), Guid.NewGuid()));
    }

    [Fact]
    public async Task The_bridge_key_is_masked_on_read_and_kept_when_not_resent()
    {
        var settings = new WhatsAppBridgeSettingsService(_store, new UpdateWhatsAppBridgeSettingsRequestValidator(), _bridge);
        await settings.UpdateAsync(new UpdateWhatsAppBridgeSettingsRequest("http://bridge", "abcdef123456", "https://api", null), Guid.NewGuid());

        var shown = await settings.GetAsync();
        Assert.True(shown.HasApiKey);
        Assert.Equal("••••3456", shown.ApiKeyHint);
        Assert.DoesNotContain("abcdef", System.Text.Json.JsonSerializer.Serialize(shown));

        await settings.UpdateAsync(new UpdateWhatsAppBridgeSettingsRequest("http://bridge", null, "https://api", null), Guid.NewGuid());
        Assert.Equal("abcdef123456", _store.Rows[WhatsAppBridgeSettingsService.ApiKeyKey]);
    }

    [Fact]
    public async Task Pilot_tenants_are_stored_and_read_back()
    {
        var settings = new WhatsAppBridgeSettingsService(_store, new UpdateWhatsAppBridgeSettingsRequestValidator(), _bridge);
        var pilot = Guid.NewGuid();

        var saved = await settings.UpdateAsync(new UpdateWhatsAppBridgeSettingsRequest("http://bridge", "k", "https://api", new[] { pilot, pilot }), Guid.NewGuid());

        Assert.Equal(new[] { pilot }, saved.PilotTenantIds);
    }

    private sealed class FakeBridge : IWhatsAppBridgeClient
    {
        public bool PingFails { get; set; }

        public Task PingAsync(BridgeEndpoint e, CancellationToken ct = default) =>
            PingFails ? throw new WhatsAppBridgeException("down") : Task.CompletedTask;

        public Task<BridgeInstance> CreateInstanceAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BridgeQr?> ConnectAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetStateAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfigureWebhookAsync(BridgeEndpoint e, string n, string u, string h, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RestartAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppSendResult> SendTextAsync(BridgeEndpoint e, string n, string to, string t, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
