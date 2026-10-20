using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;
using WhatsAppSalesAutomation.Infrastructure.Settings;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The per-tenant switch for chat reading the published product knowledge: a platform-administrator setting (a tenant override over a platform default of off),
/// invisible to other tenants, validated, and shown with the rest of a tenant's overrides. And the prompt the agent gets when nothing approved covers a message.
/// </summary>
public sealed class ProductKnowledgeServingSwitchTests : IDisposable
{
    private sealed class Snapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public Snapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class Ambient : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => TenantId is null;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Ambient _ambient = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public ProductKnowledgeServingSwitchTests()
    {
        _connection.Open();
        _db = new SqliteApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options, _ambient, new NoUser());
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ProductKnowledgeServing Serving(bool platformDefault = false) =>
        new(_db, _ambient, new Snapshot<ProductKnowledgeSettings>(new ProductKnowledgeSettings { ServeChat = platformDefault }));

    private TenantConfigOverrideProvider Overrides(bool platformDefault = false) => new(
        _db, _ambient, new Clock(), new UpdateTenantSettingsRequestValidator(),
        new Snapshot<CampaignOptions>(new CampaignOptions()), new Snapshot<MediaOptions>(new MediaOptions()), new Snapshot<MessagingOptions>(new MessagingOptions()),
        new Snapshot<AiOptions>(new AiOptions()), new Snapshot<StudioOptions>(new StudioOptions()),
        new Snapshot<ProductKnowledgeSettings>(new ProductKnowledgeSettings { ServeChat = platformDefault }));

    private Task SetAsync(Guid tenant, string? value, bool platformDefault = false) =>
        Overrides(platformDefault).SaveOverridesForTenantAsync(tenant, new UpdateTenantSettingsRequest(new Dictionary<string, string?> { ["ProductKnowledge:ServeChat"] = value }), null);

    [Fact]
    public async Task The_switch_is_off_for_every_tenant_by_default()
    {
        Assert.False(await Serving().IsEnabledForTenantAsync(_a));
        _ambient.TenantId = _a;
        Assert.False(await Serving().IsEnabledAsync());
    }

    [Fact]
    public async Task The_platform_administrator_can_switch_one_tenant_on_without_touching_any_other()
    {
        await SetAsync(_a, "true");

        Assert.True(await Serving().IsEnabledForTenantAsync(_a));
        Assert.False(await Serving().IsEnabledForTenantAsync(_b));

        _ambient.TenantId = _a;
        Assert.True(await Serving().IsEnabledAsync());
        _ambient.TenantId = _b;
        Assert.False(await Serving().IsEnabledAsync()); // another tenant's request never sees A's switch
    }

    [Fact]
    public async Task A_tenant_override_beats_the_platform_default_in_either_direction_and_a_blank_returns_to_the_default()
    {
        Assert.True(await Serving(platformDefault: true).IsEnabledForTenantAsync(_a)); // default on, no override

        await SetAsync(_a, "false", platformDefault: true);
        Assert.False(await Serving(platformDefault: true).IsEnabledForTenantAsync(_a));
        Assert.True(await Serving(platformDefault: true).IsEnabledForTenantAsync(_b));

        await SetAsync(_a, "", platformDefault: true);
        Assert.True(await Serving(platformDefault: true).IsEnabledForTenantAsync(_a));
    }

    [Fact]
    public async Task With_no_tenant_in_scope_the_platform_default_applies()
    {
        _ambient.TenantId = null;

        Assert.False(await Serving().IsEnabledAsync());
        Assert.True(await Serving(platformDefault: true).IsEnabledAsync());
    }

    [Fact]
    public async Task Only_true_or_false_can_be_saved()
    {
        await Assert.ThrowsAsync<ValidationException>(() => SetAsync(_a, "maybe"));
        await Assert.ThrowsAsync<ValidationException>(() => SetAsync(_a, "1"));

        Assert.False(await Serving().IsEnabledForTenantAsync(_a));
    }

    [Fact]
    public async Task The_switch_is_listed_with_the_tenants_other_overrides_for_the_platform_administrator_with_its_default_and_options()
    {
        await SetAsync(_a, "true");

        var categories = await Overrides().GetOverridesForTenantAsync(_a);

        var item = categories.Single(c => c.Category == "ProductKnowledge").Items.Single(i => i.Key == "ProductKnowledge:ServeChat");
        Assert.Equal(("false", "true", "true"), (item.GlobalValue, item.OverrideValue, item.EffectiveValue));
        Assert.Equal(new[] { "true", "false" }, item.Options);

        var other = (await Overrides().GetOverridesForTenantAsync(_b)).Single(c => c.Category == "ProductKnowledge").Items.Single();
        Assert.Equal((null, "false"), (other.OverrideValue, other.EffectiveValue));
    }

    // ----- what the agent is told when nothing approved covers the message --------------------------------------------------------------

    private static AiConversationContext Context(bool serving, params string[] chunks) => new(
        Guid.NewGuid(), "Asha", "What is the fee?", Array.Empty<AiConversationTurn>(), chunks.Select(c => new AiKnowledgeSnippet(Guid.NewGuid(), c, 0.9)).ToList(),
        null, new AiBusinessProfile("Clinic", null, null, null, null, null, null, ConversationGoal.Enquiry, false), Array.Empty<AiQualificationField>(),
        Array.Empty<AiCapturedField>(), Array.Empty<AiQualificationField>(), false, null, null, serving);

    [Fact]
    public void With_the_switch_on_and_nothing_approved_the_agent_is_told_it_knows_nothing_and_may_only_ask_one_question()
    {
        var prompt = AiPromptSupport.BuildUserMessage(Context(serving: true));

        Assert.Contains("NOTHING approved covers this message", prompt);
        Assert.Contains("ONE short clarifying question", prompt);
        Assert.Contains("do not answer from general knowledge", prompt);
        Assert.DoesNotContain("nothing found for this message", prompt);
    }

    [Fact]
    public void With_the_switch_off_the_empty_knowledge_wording_is_unchanged()
    {
        var prompt = AiPromptSupport.BuildUserMessage(Context(serving: false));

        Assert.Contains("nothing found for this message - do not invent anything about the business", prompt);
        Assert.DoesNotContain("NOTHING approved", prompt);
    }

    [Fact]
    public void With_approved_knowledge_the_snippets_are_shown_and_neither_empty_message_appears()
    {
        var prompt = AiPromptSupport.BuildUserMessage(Context(serving: true, "The consultation fee is Rs 500."));

        Assert.Contains("The consultation fee is Rs 500.", prompt);
        Assert.DoesNotContain("NOTHING approved", prompt);
        Assert.DoesNotContain("nothing found for this message", prompt);
    }
}
