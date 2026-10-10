using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"Verify connection": Meta is asked for the configured phone number with the stored token, the answer is
/// recorded, and any later save clears it so a verified tick always describes the credentials as they are.</summary>
public sealed class TenantWhatsAppVerificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly StubHandler _meta = new();
    private readonly TenantWhatsAppConfigProvider _provider;
    private readonly TenantWhatsAppConnectionVerifier _verifier;

    public TenantWhatsAppVerificationTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        _provider = new TenantWhatsAppConfigProvider(_db, new Ambient(_tenant), _protection, _clock, new UpdateTenantWhatsAppConfigRequestValidator());
        _verifier = new TenantWhatsAppConnectionVerifier(new HttpClient(_meta), _db, _protection, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<TenantWhatsAppConfigDto> SaveAsync(string token = "EAAG-token") =>
        _provider.SaveConfigForTenantAsync(_tenant, new UpdateTenantWhatsAppConfigRequest("1098765", "2233445", token, "app-secret", "v19.0", "https://graph.facebook.com/"), null);

    [Fact]
    public async Task A_phone_number_id_already_used_by_another_workspace_is_refused_with_a_clear_message_not_a_500()
    {
        var other = Guid.NewGuid();
        await _provider.SaveConfigForTenantAsync(
            other, new UpdateTenantWhatsAppConfigRequest("1098765", "2233445", "EAAG-other", "app-secret", "v19.0", "https://graph.facebook.com/"), null);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => SaveAsync());

        Assert.Contains("already connected to another workspace", ex.Message);
        // The same workspace saving its own number again is not a clash.
        await _provider.SaveConfigForTenantAsync(
            other, new UpdateTenantWhatsAppConfigRequest("1098765", "2233445", null, null, "v19.0", "https://graph.facebook.com/"), null);
    }

    [Fact]
    public async Task A_successful_check_records_when_and_which_number_Meta_confirmed()
    {
        await SaveAsync();
        _meta.Respond(HttpStatusCode.OK, """{"display_phone_number":"+91 98765 43210","verified_name":"Confianza IT","id":"1098765"}""");

        var result = await _verifier.VerifyAsync(_tenant);

        Assert.True(result.IsVerified);
        Assert.Equal(_clock.UtcNow, result.VerifiedAtUtc);
        Assert.Equal("+91 98765 43210", result.VerifiedDisplayPhoneNumber);
        Assert.Equal("Confianza IT", result.VerifiedName);
        Assert.Null(result.VerificationError);
    }

    [Fact]
    public async Task The_token_goes_in_the_header_and_never_in_the_url()
    {
        await SaveAsync("EAAG-secret-token");
        _meta.Respond(HttpStatusCode.OK, "{}");

        await _verifier.VerifyAsync(_tenant);

        var (uri, authorization) = Assert.Single(_meta.Requests);
        Assert.Equal("https://graph.facebook.com/v19.0/1098765?fields=display_phone_number,verified_name", uri);
        Assert.Equal("Bearer EAAG-secret-token", authorization);
        Assert.DoesNotContain("EAAG", uri);
    }

    [Fact]
    public async Task A_rejected_token_is_not_verified_and_Metas_reason_is_kept()
    {
        await SaveAsync();
        _meta.Respond(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid OAuth access token.","code":190}}""");

        var result = await _verifier.VerifyAsync(_tenant);

        Assert.False(result.IsVerified);
        Assert.Null(result.VerifiedAtUtc);
        Assert.Contains("Invalid OAuth access token. (code 190)", result.VerificationError);
    }

    [Fact]
    public async Task Saving_again_clears_an_earlier_verification()
    {
        await SaveAsync();
        _meta.Respond(HttpStatusCode.OK, "{}");
        Assert.True((await _verifier.VerifyAsync(_tenant)).IsVerified);

        var resaved = await SaveAsync("EAAG-new-token");

        Assert.False(resaved.IsVerified);
        Assert.Null(resaved.VerifiedAtUtc);
    }

    [Fact]
    public async Task There_is_nothing_to_verify_before_credentials_are_saved()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _verifier.VerifyAsync(_tenant));
        Assert.Empty(_meta.Requests);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private (HttpStatusCode Status, string Body) _next = (HttpStatusCode.OK, "{}");

        public List<(string Uri, string? Authorization)> Requests { get; } = new();

        public void Respond(HttpStatusCode status, string body) => _next = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(_next.Status) { Content = new StringContent(_next.Body, Encoding.UTF8, "application/json") });
        }
    }
}
