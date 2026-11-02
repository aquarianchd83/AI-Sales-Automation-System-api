using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The QR bridge's payloads, as observed from a real Evolution API v2.3.7 session (ids, numbers and tokens replaced).</summary>
public class EvolutionWebhookNormalizerTests
{
    private const string Token = "11111111-2222-3333-4444-555555555555";

    private static string Upsert(string key, string message = "\"conversation\":\"Hello\"", string extra = "") =>
        $$"""{"event":"messages.upsert","instance":"t-x","data":{"key":{{{key}}},"pushName":"Asha","status":"DELIVERY_ACK","message":{{{message}}},"messageType":"conversation","messageTimestamp":1793071801,"source":"android"{{extra}}},"apikey":"{{Token}}"}""";

    private static WhatsAppWebhookParseResult Parse(NormalizedBridgeEvent evt)
    {
        Assert.True(EvolutionWebhookNormalizer.TryParseCanonical(evt.Canonical!, out var result));
        return result;
    }

    [Fact]
    public void A_customers_text_message_becomes_an_inbound_message_with_the_phone_number()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(Upsert("\"remoteJid\":\"919800000001@s.whatsapp.net\",\"remoteJidAlt\":\"919800000001@s.whatsapp.net\",\"fromMe\":false,\"id\":\"AC01\",\"addressingMode\":\"lid\""));

        Assert.Equal(NormalizedBridgeEvent.EventKind.Messages, evt.Kind);
        var parsed = Parse(evt);
        var message = Assert.Single(parsed.Messages);
        Assert.Equal("AC01", message.WhatsAppMessageId);
        Assert.Equal("919800000001", message.FromPhone);
        Assert.Equal("Hello", message.TextBody);
        Assert.Equal("text", message.MessageType);
        Assert.Equal("Asha", message.ContactName);
        Assert.Equal(WhatsAppProviders.QrBridge, parsed.Provider);
    }

    [Fact]
    public void A_hidden_id_takes_the_phone_from_the_alternate_jid()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(Upsert("\"remoteJid\":\"1111111111111@lid\",\"remoteJidAlt\":\"919800000001@s.whatsapp.net\",\"fromMe\":false,\"id\":\"AC02\""));

        Assert.Equal("919800000001", Assert.Single(Parse(evt).Messages).FromPhone);
    }

    [Fact]
    public void A_hidden_id_with_no_phone_is_dropped_rather_than_creating_a_junk_customer()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(Upsert("\"remoteJid\":\"1111111111111@lid\",\"fromMe\":false,\"id\":\"AC03\""));

        Assert.Equal(NormalizedBridgeEvent.EventKind.Ignored, evt.Kind);
    }

    [Fact]
    public void Messages_typed_on_the_linked_phone_are_never_treated_as_customer_messages()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(Upsert("\"remoteJid\":\"919800000001@s.whatsapp.net\",\"fromMe\":true,\"id\":\"AC04\""));

        Assert.Equal(NormalizedBridgeEvent.EventKind.Ignored, evt.Kind);
    }

    [Fact]
    public void Group_messages_are_dropped()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(Upsert("\"remoteJid\":\"120363000000000001@g.us\",\"fromMe\":false,\"id\":\"AC05\",\"participant\":\"2222222222222@lid\""));

        Assert.Equal(NormalizedBridgeEvent.EventKind.Ignored, evt.Kind);
    }

    [Fact]
    public void A_non_text_message_is_kept_without_a_body_like_the_meta_path()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(
            """{"event":"messages.upsert","data":{"key":{"remoteJid":"919800000001@s.whatsapp.net","fromMe":false,"id":"AC06"},"message":{"imageMessage":{}},"messageType":"imageMessage","messageTimestamp":1793071801},"apikey":"t"}""");

        var message = Assert.Single(Parse(evt).Messages);
        Assert.Equal("imageMessage", message.MessageType);
        Assert.Null(message.TextBody);
    }

    [Theory]
    [InlineData("SERVER_ACK", "sent")]
    [InlineData("DELIVERY_ACK", "delivered")]
    [InlineData("READ", "read")]
    public void Delivery_statuses_of_our_own_messages_map_to_the_existing_status_words(string bridgeStatus, string expected)
    {
        var evt = EvolutionWebhookNormalizer.Normalize(
            $$"""{"event":"messages.update","data":{"keyId":"3EB05","remoteJid":"1111111111111@lid","fromMe":true,"status":"{{bridgeStatus}}","messageId":"internal"},"apikey":"t"}""");

        var status = Assert.Single(Parse(evt).Statuses);
        Assert.Equal("3EB05", status.WhatsAppMessageId);
        Assert.Equal(expected, status.Status);
    }

    [Fact]
    public void The_read_state_of_a_message_we_received_is_not_a_delivery_status_of_ours()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(
            """{"event":"messages.update","data":{"keyId":"AC7D","remoteJid":"1111111111111@lid","fromMe":false,"status":"READ"},"apikey":"t"}""");

        Assert.Equal(NormalizedBridgeEvent.EventKind.Ignored, evt.Kind);
    }

    [Fact]
    public void An_open_connection_update_reports_the_state_and_the_linked_number()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(
            $$"""{"event":"connection.update","data":{"instance":"t-x","wuid":"919800000009@s.whatsapp.net","profilePictureUrl":"https://pps.example/p.jpg","state":"open","statusReason":200},"apikey":"{{Token}}"}""");

        Assert.Equal(NormalizedBridgeEvent.EventKind.Connection, evt.Kind);
        Assert.Equal("open", evt.ConnectionState);
        Assert.Equal("919800000009", evt.ConnectedPhone);
    }

    [Fact]
    public void A_qr_event_is_ignored_and_never_stored()
    {
        var evt = EvolutionWebhookNormalizer.Normalize($$$"""{"event":"qrcode.updated","data":{"qrcode":{"code":"2@secret","base64":"data:image/png;base64,AAAA"}},"apikey":"{{{Token}}}"}""");

        Assert.Equal(NormalizedBridgeEvent.EventKind.Ignored, evt.Kind);
        Assert.Null(evt.Canonical);
    }

    [Fact]
    public void The_stored_form_never_contains_the_instance_token_or_the_encryption_material()
    {
        var evt = EvolutionWebhookNormalizer.Normalize(Upsert(
            "\"remoteJid\":\"919800000001@s.whatsapp.net\",\"fromMe\":false,\"id\":\"AC07\"",
            "\"conversation\":\"Hi\",\"messageContextInfo\":{\"messageSecret\":{\"0\":67,\"1\":137}}"));

        Assert.Equal(Token, evt.ProvidedToken);
        Assert.DoesNotContain(Token, evt.Canonical);
        Assert.DoesNotContain("messageSecret", evt.Canonical);
        Assert.DoesNotContain("apikey", evt.Canonical);
    }

    [Fact]
    public void Garbage_is_ignored_not_thrown()
    {
        Assert.Equal(NormalizedBridgeEvent.EventKind.Ignored, EvolutionWebhookNormalizer.Normalize("not json").Kind);
    }

    [Fact]
    public void A_meta_payload_is_not_mistaken_for_ours()
    {
        Assert.False(EvolutionWebhookNormalizer.TryParseCanonical("""{"object":"whatsapp_business_account","entry":[]}""", out _));
    }
}

public class WhatsAppChannelServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly FakeStore _store = new();
    private readonly FakeBridge _bridge = new();
    private readonly FakeSettings _settings = new();
    private readonly List<TimeSpan> _delays = new();

    private WhatsAppChannelService Service() => new(_store, _bridge, _settings, (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    [Fact]
    public async Task Connecting_a_new_tenant_creates_one_instance_registers_the_authenticated_webhook_and_returns_a_qr()
    {
        var status = await Service().ConnectAsync(Tenant);

        Assert.Equal(BridgeStatuses.QrCodeAvailable, status.Status);
        Assert.Equal("data:image/png;base64,QR1", status.QrBase64);
        Assert.Equal(WhatsAppChannelService.InstanceNameFor(Tenant), Assert.Single(_bridge.Created));
        var hook = Assert.Single(_bridge.Webhooks);
        Assert.StartsWith("https://api.example.com/api/v1/webhooks/whatsapp-bridge/", hook.Url);
        Assert.Equal(WhatsAppChannelService.SecretHeaderName, hook.Header);
        Assert.Equal(_store.LastCredentials!.Secret, hook.Secret);
        Assert.EndsWith(_store.LastCredentials.ChannelToken, hook.Url);
    }

    [Fact]
    public async Task The_qr_is_returned_to_the_caller_but_never_stored()
    {
        await Service().ConnectAsync(Tenant);

        Assert.DoesNotContain("QR1", _store.Dump());
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_instance_name()
    {
        Assert.NotEqual(WhatsAppChannelService.InstanceNameFor(Tenant), WhatsAppChannelService.InstanceNameFor(Guid.NewGuid()));
    }

    [Fact]
    public async Task Connecting_again_while_unpaired_asks_for_a_fresh_qr_instead_of_creating_a_second_instance()
    {
        await Service().ConnectAsync(Tenant);
        _bridge.State = "connecting";

        var again = await Service().ConnectAsync(Tenant);

        Assert.Single(_bridge.Created);
        Assert.Equal("data:image/png;base64,QR2", again.QrBase64);
    }

    [Fact]
    public async Task An_already_open_session_is_reported_connected_with_no_qr()
    {
        await Service().ConnectAsync(Tenant);
        _bridge.State = "open";

        var status = await Service().ConnectAsync(Tenant);

        Assert.Equal(BridgeStatuses.Connected, status.Status);
        Assert.Null(status.QrBase64);
    }

    [Fact]
    public async Task Displaying_a_qr_never_marks_the_channel_connected_only_the_bridges_open_state_does()
    {
        await Service().ConnectAsync(Tenant);
        Assert.NotEqual(BridgeStatuses.Connected, (await _store.GetAsync(Tenant))!.Status);

        await Service().ApplyConnectionUpdateAsync(Tenant, "connecting", null);
        Assert.NotEqual(BridgeStatuses.Connected, (await _store.GetAsync(Tenant))!.Status);

        await Service().ApplyConnectionUpdateAsync(Tenant, "open", "919800000009");
        var channel = (await _store.GetAsync(Tenant))!;
        Assert.Equal(BridgeStatuses.Connected, channel.Status);
        Assert.Equal("919800000009", channel.ConnectedPhone);
    }

    [Fact]
    public async Task Connecting_after_having_been_connected_is_a_reconnect_not_a_new_pairing()
    {
        await Service().ConnectAsync(Tenant);
        await Service().ApplyConnectionUpdateAsync(Tenant, "open", "919800000009");

        await Service().ApplyConnectionUpdateAsync(Tenant, "connecting", null);

        Assert.Equal(BridgeStatuses.Reconnecting, (await _store.GetAsync(Tenant))!.Status);
    }

    [Fact]
    public async Task An_unrecognised_state_changes_nothing()
    {
        await Service().ConnectAsync(Tenant);
        var before = (await _store.GetAsync(Tenant))!.Status;

        await Service().ApplyConnectionUpdateAsync(Tenant, "something-new", null);

        Assert.Equal(before, (await _store.GetAsync(Tenant))!.Status);
    }

    [Fact]
    public async Task A_close_state_marks_the_channel_disconnected()
    {
        await Service().ConnectAsync(Tenant);
        await Service().ApplyConnectionUpdateAsync(Tenant, "open", "919800000009");

        await Service().ApplyConnectionUpdateAsync(Tenant, "close", null);

        Assert.Equal(BridgeStatuses.Disconnected, (await _store.GetAsync(Tenant))!.Status);
    }

    [Fact]
    public async Task Disconnect_is_confirmed_by_rereading_the_state_not_by_the_logout_reply()
    {
        await Service().ConnectAsync(Tenant);
        _bridge.State = "open";
        _bridge.LogoutClosesSession = true;

        var status = await Service().DisconnectAsync(Tenant);

        Assert.True(status.Confirmed);
        Assert.Equal(BridgeStatuses.Disconnected, status.Status);
        Assert.Equal(0, _bridge.Restarts);
    }

    [Fact]
    public async Task A_logout_that_leaves_the_session_open_triggers_the_observed_restart_then_logout_sequence()
    {
        await Service().ConnectAsync(Tenant);
        _bridge.State = "open";
        _bridge.LogoutClosesSession = false;
        _bridge.RestartMovesToConnecting = true;

        var status = await Service().DisconnectAsync(Tenant);

        Assert.Equal(1, _bridge.Restarts);
        Assert.Equal(2, _bridge.Logouts);
        Assert.True(status.Confirmed);
    }

    [Fact]
    public async Task A_disconnect_the_bridge_never_honours_is_reported_unconfirmed_and_the_status_is_not_changed()
    {
        await Service().ConnectAsync(Tenant);
        await Service().ApplyConnectionUpdateAsync(Tenant, "open", "919800000009");
        _bridge.State = "open";
        _bridge.LogoutClosesSession = false;
        _bridge.RestartMovesToConnecting = false;

        var status = await Service().DisconnectAsync(Tenant);

        Assert.False(status.Confirmed);
        Assert.Equal(BridgeStatuses.Connected, status.Status);
        Assert.False(string.IsNullOrEmpty(status.LastError));
    }

    [Fact]
    public async Task Removing_deletes_the_instance_and_the_channel_only_once_the_disconnect_is_confirmed()
    {
        await Service().ConnectAsync(Tenant);
        _bridge.State = "open";
        _bridge.LogoutClosesSession = true;

        await Service().RemoveAsync(Tenant);

        Assert.Single(_bridge.Deleted);
        Assert.Null(await _store.GetAsync(Tenant));
    }

    [Fact]
    public async Task Removing_keeps_the_channel_and_the_instance_when_the_disconnect_cannot_be_confirmed()
    {
        await Service().ConnectAsync(Tenant);
        _bridge.State = "open";
        _bridge.LogoutClosesSession = false;
        _bridge.RestartMovesToConnecting = false;

        var status = await Service().RemoveAsync(Tenant);

        Assert.False(status.Confirmed);
        Assert.Empty(_bridge.Deleted);
        Assert.NotNull(await _store.GetAsync(Tenant));
    }

    [Fact]
    public async Task Without_saved_bridge_settings_nothing_is_attempted()
    {
        _settings.Endpoint = null;

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().ConnectAsync(Tenant));
        Assert.Empty(_bridge.Created);
    }

    [Fact]
    public async Task Status_of_a_tenant_with_no_channel_is_not_connected()
    {
        Assert.Equal(BridgeStatuses.NotConnected, (await Service().GetStatusAsync(Guid.NewGuid())).Status);
    }

    [Fact]
    public async Task Status_read_while_the_bridge_is_down_returns_the_stored_status_but_says_it_is_not_confirmed()
    {
        await Service().ConnectAsync(Tenant);
        await Service().ApplyConnectionUpdateAsync(Tenant, "open", "919800000009");

        _bridge.Unreachable = true;
        var down = await Service().GetStatusAsync(Tenant);

        Assert.Equal(BridgeStatuses.Connected, down.Status);
        Assert.False(down.BridgeReachable);
        Assert.Equal("The bridge could not be reached.", down.LastError);

        _bridge.Unreachable = false;
        _bridge.State = "open";
        var up = await Service().GetStatusAsync(Tenant);
        Assert.True(up.BridgeReachable);
        Assert.Null(up.LastError);
    }

    // ---- fakes ----------------------------------------------------------------------------------------------------

    private sealed class FakeSettings : IWhatsAppBridgeSettingsService
    {
        public BridgeEndpoint? Endpoint { get; set; } = new("http://bridge", "key");
        public Task<WhatsAppBridgeSettingsDto> GetAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BridgeEndpoint?> GetEndpointAsync(CancellationToken ct = default) => Task.FromResult(Endpoint);
        public Task<string> GetWebhookBaseUrlAsync(CancellationToken ct = default) => Task.FromResult("https://api.example.com");
        public Task<WhatsAppBridgeSettingsDto> UpdateAsync(UpdateWhatsAppBridgeSettingsRequest r, Guid? u, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeBridge : IWhatsAppBridgeClient
    {
        private int _qrCount;
        public string? State { get; set; }
        public bool LogoutClosesSession { get; set; } = true;
        public bool RestartMovesToConnecting { get; set; } = true;
        public int Logouts { get; private set; }
        public int Restarts { get; private set; }
        public List<string> Created { get; } = new();
        public List<string> Deleted { get; } = new();
        public List<(string Url, string Header, string Secret)> Webhooks { get; } = new();

        public Task PingAsync(BridgeEndpoint e, CancellationToken ct = default) => Task.CompletedTask;

        public Task<BridgeInstance> CreateInstanceAsync(BridgeEndpoint e, string name, CancellationToken ct = default)
        {
            Created.Add(name);
            State = "connecting";
            return Task.FromResult(new BridgeInstance(name, "instance-token", "connecting", new BridgeQr($"data:image/png;base64,QR{++_qrCount}", "2@code")));
        }

        public Task<BridgeQr?> ConnectAsync(BridgeEndpoint e, string name, CancellationToken ct = default) =>
            Task.FromResult<BridgeQr?>(new BridgeQr($"data:image/png;base64,QR{++_qrCount}", "2@code"));

        public bool Unreachable { get; set; }

        public Task<string?> GetStateAsync(BridgeEndpoint e, string name, CancellationToken ct = default) =>
            Unreachable ? throw new WhatsAppBridgeException("The bridge could not be reached.") : Task.FromResult(State);

        public Task ConfigureWebhookAsync(BridgeEndpoint e, string name, string url, string header, string secret, CancellationToken ct = default)
        {
            Webhooks.Add((url, header, secret));
            return Task.CompletedTask;
        }

        public Task LogoutAsync(BridgeEndpoint e, string name, CancellationToken ct = default)
        {
            Logouts++;
            if (LogoutClosesSession && State == "open")
                State = "close";
            return Task.CompletedTask;
        }

        public Task RestartAsync(BridgeEndpoint e, string name, CancellationToken ct = default)
        {
            Restarts++;
            if (RestartMovesToConnecting)
            {
                State = "connecting";
                LogoutClosesSession = true;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(BridgeEndpoint e, string name, CancellationToken ct = default)
        {
            Deleted.Add(name);
            State = null;
            return Task.CompletedTask;
        }

        public Task<WhatsAppSendResult> SendTextAsync(BridgeEndpoint e, string name, string to, string text, CancellationToken ct = default) =>
            Task.FromResult(new WhatsAppSendResult(true, "id", null, WhatsAppProviders.QrBridge));
    }

    private sealed class FakeStore : IWhatsAppChannelStore
    {
        private readonly Dictionary<Guid, WhatsAppChannelRecord> _rows = new();
        public WhatsAppChannelWebhookCredentials? LastCredentials { get; private set; }

        public Task<WhatsAppChannelRecord?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(tenantId, out var r) ? r : null);

        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(_rows.Keys.ToList());

        public Task<WhatsAppChannelRecord?> FindByWebhookAsync(string token, string secret, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<WhatsAppChannelWebhookCredentials> UpsertAsync(Guid tenantId, string name, string instanceToken, string status, CancellationToken ct = default)
        {
            LastCredentials = new WhatsAppChannelWebhookCredentials(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            _rows[tenantId] = new WhatsAppChannelRecord(tenantId, name, instanceToken, status, null, null, DateTime.UtcNow, DateTime.UtcNow);
            return Task.FromResult(LastCredentials);
        }

        public Task UpdateStatusAsync(Guid tenantId, string status, string? phone, string? error, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(tenantId, out var r))
                _rows[tenantId] = r with { Status = status, ConnectedPhone = phone, LastError = error };
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid tenantId, CancellationToken ct = default)
        {
            _rows.Remove(tenantId);
            return Task.CompletedTask;
        }

        public string Dump() => string.Join("|", _rows.Values.Select(r => $"{r.InstanceName},{r.Status},{r.ConnectedPhone},{r.LastError}"));
    }
}

/// <summary>The real store on a real (SQLite) database: how a webhook proves which channel, and so which tenant, it belongs to.</summary>
public sealed class WhatsAppChannelStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly WhatsAppChannelStore _store;
    private readonly Guid _tenant = Guid.NewGuid();

    public WhatsAppChannelStoreTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _store = new WhatsAppChannelStore(_db, DataProtectionProvider.Create("whatsapp-bridge-tests"), new Clock());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_webhook_is_found_only_when_both_the_path_token_and_the_secret_are_right()
    {
        var creds = await _store.UpsertAsync(_tenant, "t-1", "token", BridgeStatuses.ConnectionInitiated);

        Assert.Equal(_tenant, (await _store.FindByWebhookAsync(creds.ChannelToken, creds.Secret))!.TenantId);
        Assert.Null(await _store.FindByWebhookAsync(creds.ChannelToken, "wrong"));
        Assert.Null(await _store.FindByWebhookAsync("wrong", creds.Secret));
        Assert.Null(await _store.FindByWebhookAsync(creds.ChannelToken, ""));
        Assert.Null(await _store.FindByWebhookAsync("", creds.Secret));
    }

    [Fact]
    public async Task Another_tenants_secret_does_not_open_this_tenants_channel()
    {
        var mine = await _store.UpsertAsync(_tenant, "t-1", "token-1", BridgeStatuses.ConnectionInitiated);
        var other = await _store.UpsertAsync(Guid.NewGuid(), "t-2", "token-2", BridgeStatuses.ConnectionInitiated);

        Assert.Null(await _store.FindByWebhookAsync(mine.ChannelToken, other.Secret));
    }

    [Fact]
    public async Task Reconnecting_issues_new_webhook_credentials_and_the_old_ones_stop_working()
    {
        var first = await _store.UpsertAsync(_tenant, "t-1", "token", BridgeStatuses.ConnectionInitiated);
        var second = await _store.UpsertAsync(_tenant, "t-1", "token", BridgeStatuses.ConnectionInitiated);

        Assert.Null(await _store.FindByWebhookAsync(first.ChannelToken, first.Secret));
        Assert.NotNull(await _store.FindByWebhookAsync(second.ChannelToken, second.Secret));
    }

    [Fact]
    public async Task Secrets_are_never_stored_in_the_clear()
    {
        var creds = await _store.UpsertAsync(_tenant, "t-1", "super-secret-instance-token", BridgeStatuses.ConnectionInitiated);

        var row = await _db.TenantWhatsAppChannels.IgnoreQueryFilters().SingleAsync();
        Assert.DoesNotContain("super-secret-instance-token", row.InstanceTokenCipher);
        Assert.NotEqual(creds.ChannelToken, row.WebhookTokenHash);
        Assert.NotEqual(creds.Secret, row.WebhookSecretHash);
        Assert.Equal("super-secret-instance-token", (await _store.GetAsync(_tenant))!.InstanceToken);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class Ambient : ITenantContext
    {
        private readonly Guid _id;
        public Ambient(Guid id) => _id = id;
        public Guid? TenantId => _id;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}

/// <summary>The send boundary: which provider carries a message, and the rule that a bridge tenant is NEVER silently rerouted.</summary>
public sealed class WhatsAppServiceFactoryBridgeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly RecordingBridge _bridge = new();
    private readonly Channels _channels = new();
    private readonly WhatsAppProviderOptions _provider = new();
    private readonly WhatsAppBridgeOptions _options = new() { BaseUrl = "http://bridge", ApiKey = "key" };
    private readonly SimulatedWhatsAppClient _simulated;

    public WhatsAppServiceFactoryBridgeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _simulated = new SimulatedWhatsAppClient(new Snap<WhatsAppSettings>(new WhatsAppSettings()), NullLogger<SimulatedWhatsAppClient>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private WhatsAppServiceFactory Factory()
    {
        var configProvider = System.Reflection.DispatchProxy.Create<ITenantWhatsAppConfigProvider, CampaignSendQuotaTests.Stub>();
        ((CampaignSendQuotaTests.Stub)(object)configProvider).Handler = (m, _) =>
            m.Name == nameof(ITenantWhatsAppConfigProvider.GetForCurrentTenantAsync)
                ? Task.FromResult<TenantWhatsAppCredentials?>(null)
                : throw new NotImplementedException(m.Name);

        return new WhatsAppServiceFactory(
            configProvider, new MetaWhatsAppCloudApiClient(new HttpClient(), NullLogger<MetaWhatsAppCloudApiClient>.Instance), _simulated,
            new Ambient(_tenant), new Snap<WhatsAppProviderOptions>(_provider), new Snap<WhatsAppBridgeOptions>(_options),
            _channels, _bridge, _db, NullLogger<WhatsAppServiceFactory>.Instance);
    }

    private void UseBridge(string status = BridgeStatuses.Connected)
    {
        _provider.Active = WhatsAppProviders.QrBridge;
        _channels.Record = new WhatsAppChannelRecord(_tenant, "t-1", "tok", status, null, null, DateTime.UtcNow, DateTime.UtcNow);
    }

    [Fact]
    public async Task Meta_remains_the_default_and_the_bridge_is_never_touched()
    {
        var result = await Factory().SendTextMessageAsync("+919800000001", "hi");

        Assert.True(result.Success);
        Assert.Equal(WhatsAppProviders.Meta, result.Provider);
        Assert.Equal(0, _bridge.Sends);
    }

    [Fact]
    public async Task A_connected_bridge_tenant_sends_text_through_its_own_instance()
    {
        UseBridge();

        var result = await Factory().SendTextMessageAsync("+919800000001", "hi");

        Assert.True(result.Success);
        Assert.Equal(WhatsAppProviders.QrBridge, result.Provider);
        Assert.Equal("t-1", _bridge.LastInstance);
        Assert.Equal("hi", _bridge.LastText);
    }

    [Theory]
    [InlineData(BridgeStatuses.NotConnected)]
    [InlineData(BridgeStatuses.QrCodeAvailable)]
    [InlineData(BridgeStatuses.Disconnected)]
    [InlineData(BridgeStatuses.Reconnecting)]
    public async Task A_bridge_tenant_whose_channel_is_not_connected_fails_the_send_and_is_never_rerouted(string status)
    {
        UseBridge(status);

        var result = await Factory().SendTextMessageAsync("+919800000001", "hi");

        Assert.False(result.Success);
        Assert.Equal(0, _bridge.Sends);
    }

    [Fact]
    public async Task A_bridge_tenant_with_no_channel_at_all_fails_the_send_and_is_never_rerouted()
    {
        _provider.Active = WhatsAppProviders.QrBridge;

        var result = await Factory().SendTextMessageAsync("+919800000001", "hi");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task While_a_pilot_list_is_set_other_tenants_stay_on_their_existing_provider()
    {
        UseBridge();
        _options.PilotTenantIds.Add(Guid.NewGuid());

        var result = await Factory().SendTextMessageAsync("+919800000001", "hi");

        Assert.Equal(WhatsAppProviders.Meta, result.Provider);
        Assert.Equal(0, _bridge.Sends);
    }

    [Fact]
    public async Task A_listed_pilot_tenant_uses_the_bridge()
    {
        UseBridge();
        _options.PilotTenantIds.Add(_tenant);

        Assert.Equal(WhatsAppProviders.QrBridge, (await Factory().SendTextMessageAsync("+919800000001", "hi")).Provider);
    }

    [Fact]
    public async Task A_template_send_is_filled_in_locally_and_sent_as_plain_text()
    {
        UseBridge();
        _db.MessageTemplates.Add(new MessageTemplate
        {
            Name = "welcome", WhatsAppTemplateName = "welcome_v1", BodyText = "Hi {{FirstName}}, call {{PhoneNumber}}. Bye {{FirstName}}",
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved
        });
        await _db.SaveChangesAsync();

        // Positional values in first-occurrence order: FirstName, PhoneNumber.
        var result = await Factory().SendTemplateMessageAsync("+919800000001", "welcome_v1", "en", new[] { "Asha", "+919800000001" });

        Assert.True(result.Success);
        Assert.Equal("Hi Asha, call +919800000001. Bye Asha", _bridge.LastText);
    }

    [Fact]
    public async Task A_template_the_tenant_does_not_have_fails_rather_than_sending_something_else()
    {
        UseBridge();

        var result = await Factory().SendTemplateMessageAsync("+919800000001", "missing", "en", Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(0, _bridge.Sends);
    }

    [Fact]
    public async Task Template_review_calls_are_not_made_to_the_bridge()
    {
        UseBridge();
        var factory = Factory();

        Assert.Empty(await factory.GetMessageTemplatesAsync());
        Assert.False((await factory.CreateMessageTemplateAsync(new WhatsAppTemplateSubmission("n", "en", "MARKETING", "b", Array.Empty<string>()))).Success);
    }

    [Fact]
    public async Task An_unconfigured_bridge_pauses_sending_instead_of_falling_back()
    {
        UseBridge();
        _options.ApiKey = string.Empty;

        var result = await Factory().SendTextMessageAsync("+919800000001", "hi");

        Assert.False(result.Success);
    }

    private sealed class Snap<T> : IOptionsSnapshot<T> where T : class
    {
        public Snap(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    private sealed class Channels : IWhatsAppChannelStore
    {
        public WhatsAppChannelRecord? Record { get; set; }
        public Task<WhatsAppChannelRecord?> GetAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(Record);
        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelRecord?> FindByWebhookAsync(string t, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelWebhookCredentials> UpsertAsync(Guid t, string n, string i, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(Guid t, string s, string? p, string? e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class RecordingBridge : IWhatsAppBridgeClient
    {
        public int Sends { get; private set; }
        public string? LastInstance { get; private set; }
        public string? LastText { get; private set; }

        public Task<WhatsAppSendResult> SendTextAsync(BridgeEndpoint e, string instance, string to, string text, CancellationToken ct = default)
        {
            Sends++;
            LastInstance = instance;
            LastText = text;
            return Task.FromResult(new WhatsAppSendResult(true, "bridge-id", null, WhatsAppProviders.QrBridge));
        }

        public Task PingAsync(BridgeEndpoint e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BridgeInstance> CreateInstanceAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BridgeQr?> ConnectAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetStateAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfigureWebhookAsync(BridgeEndpoint e, string n, string u, string h, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RestartAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(BridgeEndpoint e, string n, CancellationToken ct = default) => throw new NotImplementedException();
    }


    private sealed class Ambient : ITenantContext
    {
        private readonly Guid _id;
        public Ambient(Guid id) => _id = id;
        public Guid? TenantId => _id;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
