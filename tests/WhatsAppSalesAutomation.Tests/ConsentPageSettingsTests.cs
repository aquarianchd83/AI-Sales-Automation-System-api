using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The tenant's WhatsApp Consent Page Settings: what may be saved, what may be published, and that each tenant only ever touches its own.</summary>
public sealed class ConsentPageSettingsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly TestClock _clock = new();
    private readonly Tenant _a = new() { Name = "Acme Eye Care", Slug = "acme", ProductName = "eye tests" };
    private readonly Tenant _b = new() { Name = "Beta Dental", Slug = "beta" };
    private readonly Ctx _ambient;
    private readonly SqliteApplicationDbContext _db;

    public ConsentPageSettingsTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _ambient = new Ctx(_a.Id);
        _db = new SqliteApplicationDbContext(_options, _ambient, new AnonymousUser()) { StampTenantId = _a.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.AddRange(_a, _b);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ConsentPageSettingsService Service() => new(_db, _ambient, _clock);

    private ConsentPageGate Gate(string? baseUrl = "https://app.example.com") =>
        new(_db, new RecordingNotifier(), Options.Create(new ConsentPageOptions { PublicBaseUrl = baseUrl }), NullLogger<ConsentPageGate>.Instance);

    private static SaveConsentPageSettingsRequest Valid(string name = "Acme Eye Care") =>
        new(name, "Eye tests and glasses for the whole family.", null, null, "https://acme.example/privacy", "https://acme.example/terms");

    // ------------------------------------------------------------------------------------------------ saving

    [Fact]
    public async Task A_new_tenant_has_an_incomplete_page_with_the_default_wording_ready_to_use()
    {
        var settings = await Service().GetAsync();

        Assert.Equal(ConsentPageStatuses.Incomplete, settings.Status);
        Assert.Equal(0, settings.PublishedVersion);
        Assert.Contains(settings.Issues, i => i.Field == "businessName");
        Assert.Contains(settings.Issues, i => i.Field == "productDescription");
        Assert.StartsWith("I agree to receive WhatsApp messages from [Business Name] about eye tests,", settings.DefaultConsentStatement);
        Assert.EndsWith("I understand that I can opt out at any time.", settings.DefaultConsentStatement);
    }

    [Fact]
    public async Task An_incomplete_draft_can_be_saved_but_is_not_published_by_saving()
    {
        var saved = await Service().SaveAsync(new SaveConsentPageSettingsRequest("Acme", null, null, null, null, null));

        Assert.Equal(ConsentPageStatuses.Incomplete, saved.Status);
        Assert.Equal("Acme", saved.BusinessName);
        Assert.Equal(0, saved.PublishedVersion);
        Assert.Empty(await _db.ConsentPageVersions.ToListAsync());
    }

    [Theory]
    [InlineData("http://acme.example/privacy")]             // not https
    [InlineData("javascript:alert(1)")]
    [InlineData("//acme.example/privacy")]
    [InlineData("https://user:pass@acme.example/privacy")]  // credentials in a link
    [InlineData("https://acme/privacy")]                     // no real host
    [InlineData("https://acme.example/pri vacy")]
    public async Task Links_that_are_not_full_https_addresses_are_refused_on_the_server(string url)
    {
        var request = Valid() with { PrivacyPolicyUrl = url };

        var error = await Assert.ThrowsAsync<ValidationException>(() => Service().SaveAsync(request));

        Assert.Contains(error.Errors, e => e.PropertyName == "privacyPolicyUrl");
        Assert.Empty(await _db.TenantConsentPages.ToListAsync());
    }

    [Fact]
    public async Task Markup_and_over_long_text_are_refused()
    {
        var markup = Valid() with { AdditionalInfo = "Visit us <script>alert(1)</script>" };
        var tooLong = Valid() with { ProductDescription = new string('x', ConsentPageRules.MaxDescription + 1) };
        var multiLineName = Valid("Acme\nEye Care");

        Assert.Contains((await Assert.ThrowsAsync<ValidationException>(() => Service().SaveAsync(markup))).Errors, e => e.PropertyName == "additionalInfo");
        Assert.Contains((await Assert.ThrowsAsync<ValidationException>(() => Service().SaveAsync(tooLong))).Errors, e => e.PropertyName == "productDescription");
        Assert.Contains((await Assert.ThrowsAsync<ValidationException>(() => Service().SaveAsync(multiLineName))).Errors, e => e.PropertyName == "businessName");
    }

    // ------------------------------------------------------------------------------------------------ publishing

    [Fact]
    public async Task Publishing_an_incomplete_page_is_refused_and_names_what_is_missing()
    {
        await Service().SaveAsync(new SaveConsentPageSettingsRequest("Acme", null, null, null, null, null));

        var error = await Assert.ThrowsAsync<ValidationException>(() => Service().PublishAsync());

        Assert.Contains(error.Errors, e => e.PropertyName == "productDescription");
        Assert.Empty(await _db.ConsentPageVersions.ToListAsync());
    }

    [Fact]
    public async Task A_consent_statement_that_never_mentions_whatsapp_cannot_be_published()
    {
        await Service().SaveAsync(Valid() with { ConsentStatement = "I agree to hear from [Business Name]." });

        var error = await Assert.ThrowsAsync<ValidationException>(() => Service().PublishAsync());

        Assert.Contains(error.Errors, e => e.PropertyName == "consentStatement");
    }

    [Fact]
    public async Task Publishing_makes_an_immutable_snapshot_with_the_statement_resolved_and_the_gate_opens()
    {
        Assert.False((await Gate().EvaluateAsync(_a.Id)).IsReady);
        await Service().SaveAsync(Valid());

        var published = await Service().PublishAsync();

        Assert.Equal(ConsentPageStatuses.Published, published.Status);
        Assert.Equal(1, published.PublishedVersion);
        Assert.False(published.HasUnpublishedChanges);
        var version = await _db.ConsentPageVersions.AsNoTracking().SingleAsync();
        Assert.Equal(1, version.Version);
        Assert.Equal(
            "I agree to receive WhatsApp messages from Acme Eye Care about eye tests, including relevant follow-ups. I understand that I can opt out at any time.",
            version.ConsentStatement);

        var readiness = await Gate().EvaluateAsync(_a.Id);
        Assert.True(readiness.IsReady);
        Assert.Equal(1, readiness.Live!.Version);
    }

    [Fact]
    public async Task Editing_after_publishing_leaves_the_live_version_alone_until_republished()
    {
        await Service().SaveAsync(Valid());
        await Service().PublishAsync();

        var edited = await Service().SaveAsync(Valid("Acme Optical") with { ConsentStatement = "I agree to receive WhatsApp offers from [Business Name]." });

        Assert.True(edited.HasUnpublishedChanges);
        Assert.Equal(ConsentPageStatuses.Published, edited.Status);
        Assert.Equal("Acme Eye Care", (await Gate().EvaluateAsync(_a.Id)).Live!.BusinessName);   // customers still see version 1

        var republished = await Service().PublishAsync();

        Assert.Equal(2, republished.PublishedVersion);
        var versions = await _db.ConsentPageVersions.AsNoTracking().OrderBy(v => v.Version).ToListAsync();
        Assert.Equal(new[] { "Acme Eye Care", "Acme Optical" }, versions.Select(v => v.BusinessName));
        Assert.Equal(
            "I agree to receive WhatsApp messages from Acme Eye Care about eye tests, including relevant follow-ups. I understand that I can opt out at any time.",
            versions[0].ConsentStatement);   // version 1's words are untouched
        Assert.Equal("I agree to receive WhatsApp offers from Acme Optical.", versions[1].ConsentStatement);
    }

    [Fact]
    public async Task Switching_the_page_off_closes_the_gate_and_publishing_again_is_needed_only_if_never_published()
    {
        await Service().SaveAsync(Valid());
        await Service().PublishAsync();

        var off = await Service().SetActiveAsync(false);
        Assert.Equal(ConsentPageStatuses.Inactive, off.Status);
        Assert.Equal(ConsentPageReadinessStates.Inactive, (await Gate().EvaluateAsync(_a.Id)).State);

        var on = await Service().SetActiveAsync(true);
        Assert.Equal(ConsentPageStatuses.Published, on.Status);
        Assert.True((await Gate().EvaluateAsync(_a.Id)).IsReady);
    }

    [Fact]
    public async Task A_page_that_was_never_published_cannot_be_switched_on()
    {
        await Service().SaveAsync(Valid());

        await Assert.ThrowsAsync<ConflictException>(() => Service().SetActiveAsync(true));
    }

    [Fact]
    public async Task Preview_shows_the_draft_as_a_customer_would_see_it_and_stores_nothing()
    {
        await Service().SaveAsync(Valid());

        var preview = await Service().PreviewAsync();

        Assert.Equal("Acme Eye Care", preview.Page.BusinessName);
        Assert.Contains("Acme Eye Care", preview.Page.ConsentStatement);
        Assert.Equal(0, preview.Page.Version);
        Assert.Empty(preview.Issues);
        Assert.Empty(await _db.ConsentPageVersions.ToListAsync());
        Assert.Equal(0, (await _db.TenantConsentPages.AsNoTracking().SingleAsync()).PublishedVersion);
    }

    [Fact]
    public async Task Without_a_public_address_the_gate_reports_a_platform_problem_not_a_tenant_one()
    {
        await Service().SaveAsync(Valid());
        await Service().PublishAsync();

        var readiness = await Gate(baseUrl: null).EvaluateAsync(_a.Id);

        Assert.False(readiness.IsReady);
        Assert.True(readiness.PlatformProblem);
    }

    // ------------------------------------------------------------------------------------------------ tenants are separate

    [Fact]
    public async Task One_tenant_cannot_see_change_or_publish_anothers_page()
    {
        await Service().SaveAsync(Valid());
        await Service().PublishAsync();

        _ambient.SetTenant(_b.Id);
        var seenByB = await Service().GetAsync();

        Assert.Null(seenByB.BusinessName);
        Assert.Equal(ConsentPageStatuses.Incomplete, seenByB.Status);
        Assert.Equal(0, seenByB.PublishedVersion);
        Assert.False((await Gate().EvaluateAsync(_b.Id)).IsReady);

        // B's own save creates B's own page and leaves A's alone.
        await Service().SaveAsync(Valid("Beta Dental"));
        await Assert.ThrowsAsync<ConflictException>(() => Service().SetActiveAsync(true));   // B has not published

        _ambient.SetTenant(_a.Id);
        Assert.Equal("Acme Eye Care", (await Service().GetAsync()).BusinessName);
        Assert.True((await Gate().EvaluateAsync(_a.Id)).IsReady);
        Assert.Equal(2, await _db.TenantConsentPages.IgnoreQueryFilters().CountAsync());
    }

    private sealed class Ctx : ITenantContext
    {
        public Ctx(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
