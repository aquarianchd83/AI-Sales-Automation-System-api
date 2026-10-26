using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

public class WhatsAppProviderSettingsTests
{
    private readonly MemoryStore _store = new();

    private WhatsAppProviderSettingsService Service() =>
        new(_store, new UpdateWhatsAppProviderRequestValidator());

    [Fact]
    public async Task Nothing_stored_means_Meta_is_active()
    {
        Assert.Equal(WhatsAppProviders.Meta, await Service().GetActiveAsync());
        Assert.True((await Service().GetAsync()).Providers.Single(p => p.Id == WhatsAppProviders.Meta).Available);
    }

    [Fact]
    public async Task The_bridge_is_listed_but_not_available_yet()
    {
        var bridge = (await Service().GetAsync()).Providers.Single(p => p.Id == WhatsAppProviders.QrBridge);
        Assert.False(bridge.Available);
        Assert.False(string.IsNullOrEmpty(bridge.UnavailableReason));
    }

    [Fact]
    public async Task Choosing_the_bridge_is_refused_and_leaves_the_active_provider_alone()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().UpdateAsync(new UpdateWhatsAppProviderRequest(WhatsAppProviders.QrBridge), Guid.NewGuid()));

        Assert.DoesNotContain(WhatsAppProviderSettingsService.ActiveKey, _store.Rows.Keys);
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

    [Fact]
    public async Task Saving_Meta_reports_the_previous_provider_for_the_audit_log()
    {
        var change = await Service().UpdateAsync(new UpdateWhatsAppProviderRequest(WhatsAppProviders.Meta), Guid.NewGuid());

        Assert.Equal(WhatsAppProviders.Meta, change.Previous);
        Assert.Equal(WhatsAppProviders.Meta, change.Settings.Active);
    }

    [Fact]
    public async Task A_stored_bridge_value_is_read_back_as_the_bridge()
    {
        // Not reachable through the API today; proves the read side is already right for when the adapter ships.
        _store.Rows[WhatsAppProviderSettingsService.ActiveKey] = "qrbridge";

        Assert.Equal(WhatsAppProviders.QrBridge, await Service().GetActiveAsync());
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
