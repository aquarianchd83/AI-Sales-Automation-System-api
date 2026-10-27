using FluentValidation;
using FluentValidation.Results;

namespace WhatsAppSalesAutomation.Application.WhatsAppBridge;

/// <summary>One tenant's QR channel as stored. The instance token is decrypted here and must never be logged or returned over the API.</summary>
public record WhatsAppChannelRecord(
    Guid TenantId, string InstanceName, string InstanceToken, string Status, string? ConnectedPhone, string? LastError,
    DateTime StatusUpdatedAtUtc, DateTime CreatedAtUtc);

/// <summary>The one-time plaintext of a freshly issued webhook credential pair. Only hashes are stored, so this is the only moment it exists.</summary>
public record WhatsAppChannelWebhookCredentials(string ChannelToken, string Secret);

/// <summary>Persistence for tenant channels - implemented in Infrastructure, reached only through here (the entity is not on the shared DbContext interface).</summary>
public interface IWhatsAppChannelStore
{
    Task<WhatsAppChannelRecord?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Every tenant that has a channel - for the health pass, which has no tenant in scope.</summary>
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds the channel a webhook claims to belong to, only when BOTH the path token and the header secret match their stored hashes.
    /// This is how a webhook's tenant is established: never from anything in the payload.</summary>
    Task<WhatsAppChannelRecord?> FindByWebhookAsync(string channelToken, string secret, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates the channel and issues a NEW webhook credential pair, invalidating any earlier one.</summary>
    Task<WhatsAppChannelWebhookCredentials> UpsertAsync(Guid tenantId, string instanceName, string instanceToken, string status, CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(Guid tenantId, string status, string? connectedPhone, string? lastError, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <param name="Qr">Present only while pairing: temporary authentication material, shown once and never persisted.</param>
/// <param name="Confirmed">False when a disconnect was requested but the bridge still reports the session open.</param>
public record WhatsAppChannelStatusDto(
    string Status, string? ConnectedPhone, string? LastError, DateTime? StatusUpdatedAtUtc, string? QrBase64, int? QrRefreshSeconds, bool Confirmed = true);

public interface IWhatsAppChannelService
{
    Task<WhatsAppChannelStatusDto> GetStatusAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Starts (or resumes) pairing and returns a QR. Already connected: returns the status with no QR.</summary>
    Task<WhatsAppChannelStatusDto> ConnectAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Logs the session out and RE-READS the bridge's state to confirm - a success reply alone is not proof.</summary>
    Task<WhatsAppChannelStatusDto> DisconnectAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Disconnects, then deletes the bridge instance and the stored channel. The channel is kept when the disconnect could not be confirmed.</summary>
    Task<WhatsAppChannelStatusDto> RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Applies a bridge <c>connection.update</c>. An unknown state changes nothing.</summary>
    Task ApplyConnectionUpdateAsync(Guid tenantId, string? providerState, string? connectedPhone, CancellationToken cancellationToken = default);
}

public class WhatsAppChannelService : IWhatsAppChannelService
{
    public const string SecretHeaderName = "X-Bridge-Secret";
    public const string WebhookRoute = "/api/v1/webhooks/whatsapp-bridge/";

    /// <summary>Observed gap between <c>qrcode.updated</c> events on Evolution API v2.3.7 - a UI hint, not a documented guarantee.</summary>
    public const int ObservedQrRefreshSeconds = 45;

    private const int DisconnectChecks = 4;

    private readonly IWhatsAppChannelStore _store;
    private readonly IWhatsAppBridgeClient _client;
    private readonly IWhatsAppBridgeSettingsService _settings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public WhatsAppChannelService(IWhatsAppChannelStore store, IWhatsAppBridgeClient client, IWhatsAppBridgeSettingsService settings)
        : this(store, client, settings, Task.Delay)
    {
    }

    /// <summary>Test seam for the wait between disconnect checks.</summary>
    internal WhatsAppChannelService(IWhatsAppChannelStore store, IWhatsAppBridgeClient client, IWhatsAppBridgeSettingsService settings, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _store = store;
        _client = client;
        _settings = settings;
        _delay = delay;
    }

    /// <summary>One instance per tenant, named from the tenant id so two tenants can never share a session.</summary>
    public static string InstanceNameFor(Guid tenantId) => $"t-{tenantId:N}";

    public async Task<WhatsAppChannelStatusDto> GetStatusAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var channel = await _store.GetAsync(tenantId, cancellationToken);
        if (channel is null)
            return new WhatsAppChannelStatusDto(BridgeStatuses.NotConnected, null, null, null, null, null);

        var endpoint = await _settings.GetEndpointAsync(cancellationToken);
        if (endpoint is null)
            return ToDto(channel);

        // The stored status is what webhooks last said; reading the bridge keeps the screen honest when one was missed.
        try
        {
            var live = BridgeStatuses.FromProviderState(await _client.GetStateAsync(endpoint, channel.InstanceName, cancellationToken), channel.Status);
            if (live is not null && live != channel.Status && !(live == BridgeStatuses.AwaitingAuthentication && channel.Status == BridgeStatuses.QrCodeAvailable))
            {
                await _store.UpdateStatusAsync(tenantId, live, channel.ConnectedPhone, null, cancellationToken);
                channel = (await _store.GetAsync(tenantId, cancellationToken)) ?? channel;
            }
        }
        catch (WhatsAppBridgeException ex)
        {
            return ToDto(channel) with { LastError = ex.Message };
        }

        return ToDto(channel);
    }

    public async Task<WhatsAppChannelStatusDto> ConnectAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var endpoint = await RequireEndpointAsync(cancellationToken);
        var channel = await _store.GetAsync(tenantId, cancellationToken);
        var state = channel is null ? null : await _client.GetStateAsync(endpoint, channel.InstanceName, cancellationToken);

        if (string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
            return ToDto(channel!) with { Status = BridgeStatuses.Connected };

        BridgeQr? qr;
        if (channel is not null && state is not null)
        {
            // Instance exists but is not paired: just ask for a fresh code.
            qr = await _client.ConnectAsync(endpoint, channel.InstanceName, cancellationToken);
        }
        else
        {
            var instanceName = InstanceNameFor(tenantId);
            var created = await _client.CreateInstanceAsync(endpoint, instanceName, cancellationToken);
            if (string.IsNullOrEmpty(created.InstanceToken))
                throw new WhatsAppBridgeException("The bridge did not return an instance token.");

            var credentials = await _store.UpsertAsync(tenantId, instanceName, created.InstanceToken, BridgeStatuses.ConnectionInitiated, cancellationToken);
            await ConfigureWebhookAsync(endpoint, instanceName, credentials, cancellationToken);
            qr = created.Qr;
        }

        var status = string.IsNullOrEmpty(qr?.Base64) ? BridgeStatuses.ConnectionInitiated : BridgeStatuses.QrCodeAvailable;
        await _store.UpdateStatusAsync(tenantId, status, null, null, cancellationToken);
        var current = (await _store.GetAsync(tenantId, cancellationToken))!;
        return ToDto(current) with { QrBase64 = qr?.Base64, QrRefreshSeconds = ObservedQrRefreshSeconds };
    }

    public async Task<WhatsAppChannelStatusDto> DisconnectAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var channel = await _store.GetAsync(tenantId, cancellationToken);
        if (channel is null)
            return new WhatsAppChannelStatusDto(BridgeStatuses.NotConnected, null, null, null, null, null);

        var endpoint = await RequireEndpointAsync(cancellationToken);
        var confirmed = await LogoutAndConfirmAsync(endpoint, channel.InstanceName, cancellationToken);

        if (confirmed)
            await _store.UpdateStatusAsync(tenantId, BridgeStatuses.Disconnected, null, null, cancellationToken);
        else
            await _store.UpdateStatusAsync(tenantId, channel.Status, channel.ConnectedPhone, "The bridge still reports this session as open. Remove the linked device on the phone, then try again.", cancellationToken);

        return ToDto((await _store.GetAsync(tenantId, cancellationToken))!) with { Confirmed = confirmed };
    }

    public async Task<WhatsAppChannelStatusDto> RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var channel = await _store.GetAsync(tenantId, cancellationToken);
        if (channel is null)
            return new WhatsAppChannelStatusDto(BridgeStatuses.NotConnected, null, null, null, null, null);

        var endpoint = await RequireEndpointAsync(cancellationToken);
        var confirmed = await LogoutAndConfirmAsync(endpoint, channel.InstanceName, cancellationToken);
        if (!confirmed)
        {
            await _store.UpdateStatusAsync(tenantId, channel.Status, channel.ConnectedPhone, "The bridge still reports this session as open, so the channel was not removed.", cancellationToken);
            return ToDto((await _store.GetAsync(tenantId, cancellationToken))!) with { Confirmed = false };
        }

        await _client.DeleteAsync(endpoint, channel.InstanceName, cancellationToken);
        await _store.RemoveAsync(tenantId, cancellationToken);
        return new WhatsAppChannelStatusDto(BridgeStatuses.NotConnected, null, null, null, null, null);
    }

    public async Task ApplyConnectionUpdateAsync(Guid tenantId, string? providerState, string? connectedPhone, CancellationToken cancellationToken = default)
    {
        var channel = await _store.GetAsync(tenantId, cancellationToken);
        if (channel is null)
            return;

        var next = BridgeStatuses.FromProviderState(providerState, channel.Status);
        if (next is null)
            return;

        // A pairing in progress keeps its more specific status while the bridge repeats "connecting" as the QR rotates.
        if (next == BridgeStatuses.AwaitingAuthentication && channel.Status is BridgeStatuses.QrCodeAvailable or BridgeStatuses.ConnectionInitiated)
            return;

        await _store.UpdateStatusAsync(tenantId, next, next == BridgeStatuses.Connected ? connectedPhone ?? channel.ConnectedPhone : channel.ConnectedPhone, null, cancellationToken);
    }

    /// <summary>A logout reply is not trusted (an open instance answered success and stayed open): re-read the state, and if it is still open
    /// restart once and log out again - the sequence that was observed to work.</summary>
    private async Task<bool> LogoutAndConfirmAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken)
    {
        await _client.LogoutAsync(endpoint, instanceName, cancellationToken);
        if (await WaitUntilNotOpenAsync(endpoint, instanceName, cancellationToken))
            return true;

        await _client.RestartAsync(endpoint, instanceName, cancellationToken);
        await _delay(TimeSpan.FromSeconds(3), cancellationToken);
        await _client.LogoutAsync(endpoint, instanceName, cancellationToken);
        return await WaitUntilNotOpenAsync(endpoint, instanceName, cancellationToken);
    }

    private async Task<bool> WaitUntilNotOpenAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken)
    {
        for (var i = 0; i < DisconnectChecks; i++)
        {
            var state = await _client.GetStateAsync(endpoint, instanceName, cancellationToken);
            if (!string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
                return true;

            await _delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }

    private async Task ConfigureWebhookAsync(BridgeEndpoint endpoint, string instanceName, WhatsAppChannelWebhookCredentials credentials, CancellationToken cancellationToken)
    {
        var webhookBase = await _settings.GetWebhookBaseUrlAsync(cancellationToken);
        if (webhookBase.Length == 0)
            throw new ValidationException(new[] { new ValidationFailure("WebhookBaseUrl", "Set the webhook address in the bridge settings first.") });

        await _client.ConfigureWebhookAsync(endpoint, instanceName, $"{webhookBase}{WebhookRoute}{credentials.ChannelToken}", SecretHeaderName, credentials.Secret, cancellationToken);
    }

    private async Task<BridgeEndpoint> RequireEndpointAsync(CancellationToken cancellationToken) =>
        await _settings.GetEndpointAsync(cancellationToken)
        ?? throw new ValidationException(new[] { new ValidationFailure("BaseUrl", "Set the bridge address and key in the bridge settings first.") });

    private static WhatsAppChannelStatusDto ToDto(WhatsAppChannelRecord channel) =>
        new(channel.Status, channel.ConnectedPhone, channel.LastError, channel.StatusUpdatedAtUtc, null, null);
}
