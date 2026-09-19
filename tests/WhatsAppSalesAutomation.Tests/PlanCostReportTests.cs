using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The consolidated cost-and-margin report a plan is designed against: unit costs come from the Configuration
/// page's charges, so the operator no longer works them out by hand.</summary>
public sealed class PlanCostReportTests : IDisposable
{
    private sealed class Store : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Rows);

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Store _store = new();
    private readonly TestClock _clock = new();

    public PlanCostReportTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private PlanCostReportService Service(string? aiDefault = "OpenAI/gpt-5-mini", string? leadDefault = "claude-sonnet-5", CostAssumptionsOptions? costing = null) =>
        new(_db, new CountryAvailability(_store), _clock,
            new FixedOptions<WhatsAppPricingOptions>(new()),
            new FixedOptions<LeadDiscoveryPricingOptions>(new() { DefaultModel = leadDefault }),
            new FixedOptions<AiPricingOptions>(new() { DefaultModel = aiDefault }),
            new FixedOptions<FxOptions>(new()),
            new FixedOptions<CostAssumptionsOptions>(costing ?? new()));

    // 100% marketing, 1000 AI prompt + 500 completion tokens, 10,000 + 1,000 tokens and one search per candidate.
    private static readonly PlanCostAssumptionsDto Simple = new(100, 0, 0, 1000, 500, 10_000, 1_000, 1m);

    // US $59, India Rs 4,897 and Germany EUR 54: the countries this plan is sold in. Everywhere else it is not sold.
    private static readonly CountryPriceInput[] Prices = { new("US", 59m), new("IN", 4897m), new("DE", 54m) };

    private static PlanCostReportRequest Request(params PlanQuotaInput[] quotas) => new(quotas, Prices, Simple);

    [Fact]
    public async Task Each_quota_is_costed_from_the_configured_charges_and_added_up_per_country()
    {
        var report = await Service().BuildAsync(Request(
            new PlanQuotaInput(QuotaType.WhatsAppMessages, 1000),
            new PlanQuotaInput(QuotaType.AiConversations, 200),
            new PlanQuotaInput(QuotaType.LeadCandidates, 50)));

        // AI: 1K prompt at 0.00025 + 0.5K completion at 0.002 = 0.00125 a conversation, x200.
        Assert.Equal(0.00125m, report.Ai.CostPerConversationUsd);
        Assert.Equal(0.25m, report.Ai.TotalUsd);
        Assert.Equal("OpenAI:gpt-5-mini", report.Ai.Model);
        // Leads: (10,000 x $2 + 1,000 x $10) per million = 0.03, plus one search at $10 per thousand = 0.01.
        Assert.Equal(0.04m, report.Leads.CostPerCandidateUsd);
        Assert.Equal(2m, report.Leads.TotalUsd);

        // Marketing weighs 1, so 1,000 pool units are 1,000 messages; US marketing is $0.025 each.
        Assert.Equal(1000m, report.WhatsAppMessagesEstimate);
        var us = report.Countries.Single(c => c.CountryCode == "US");
        Assert.Equal(25m, us.WhatsAppCostUsd);
        Assert.Equal(27.25m, us.TotalCostUsd);
        Assert.Equal(59m, us.PriceLocal);
        Assert.Equal(31.75m, us.MarginLocal);
        Assert.Equal(53.8m, us.MarginPercent);
    }

    [Fact]
    public async Task With_no_assumptions_in_the_request_the_plan_is_priced_from_the_configured_typical_usage()
    {
        // Typical use set once on the Configuration page: all marketing, and the same token sizes as the tests above.
        var costing = new CostAssumptionsOptions
        {
            MarketingSharePercent = 100, UtilitySharePercent = 0, AuthenticationSharePercent = 0,
            PromptTokensPerConversation = 1000, CompletionTokensPerConversation = 500,
            InputTokensPerCandidate = 10_000, OutputTokensPerCandidate = 1_000, WebSearchesPerCandidate = 1m
        };

        var report = await Service(costing: costing).BuildAsync(new PlanCostReportRequest(
            new[] { new PlanQuotaInput(QuotaType.WhatsAppMessages, 1000), new PlanQuotaInput(QuotaType.AiConversations, 200), new PlanQuotaInput(QuotaType.LeadCandidates, 50) },
            Prices));

        Assert.Equal(27.25m, report.Countries.Single(c => c.CountryCode == "US").TotalCostUsd);
        Assert.Equal(100m, report.Assumptions.MarketingSharePercent);
        Assert.Equal(1000, report.Assumptions.PromptTokensPerConversation);
    }

    [Fact]
    public async Task Whatsapp_cost_follows_the_country_because_meta_prices_by_country()
    {
        var report = await Service().BuildAsync(Request(new PlanQuotaInput(QuotaType.WhatsAppMessages, 1000)));

        var india = report.Countries.Single(c => c.CountryCode == "IN");
        var germany = report.Countries.Single(c => c.CountryCode == "DE");
        Assert.Equal(9.9m, india.WhatsAppCostUsd);      // 1000 x $0.0099
        Assert.Equal(136.5m, germany.WhatsAppCostUsd);  // 1000 x $0.1365
        // India is quoted in rupees: the USD price converted at 83, and the cost converted the same way.
        Assert.Equal(4897m, india.PriceLocal);
        Assert.Equal(821.7m, india.CostLocal);
    }

    [Fact]
    public async Task The_category_mix_and_quota_weights_decide_how_many_messages_a_pool_unit_buys()
    {
        // Weights follow the message prices: utility costs 0.004 against marketing's 0.025, so it weighs 0.16. 50% marketing + 50% utility
        // is 0.58 pool units a message, so 1,000 units = about 1,724 messages.
        var mix = Simple with { MarketingSharePercent = 50, UtilitySharePercent = 50 };
        var report = await Service().BuildAsync(new PlanCostReportRequest(
            new[] { new PlanQuotaInput(QuotaType.WhatsAppMessages, 1000) }, Prices, mix));

        Assert.Equal(1724m, report.WhatsAppMessagesEstimate);
        Assert.Equal(862m, report.WhatsAppCategories.Single(c => c.Category == "Marketing").Messages);
        Assert.Equal(862m, report.WhatsAppCategories.Single(c => c.Category == "Utility").Messages);
        Assert.Equal(0.16m, report.WhatsAppCategories.Single(c => c.Category == "Utility").QuotaWeight);
        // Whatever the mix, a pool unit costs one marketing message at the reference prices, so 1,000 units cost 1,000 x $0.025.
        Assert.Equal(25m, report.Countries.Single(c => c.CountryCode == "US").WhatsAppCostUsd);
    }

    [Fact]
    public async Task A_custom_country_price_is_used_as_it_is_and_a_losing_country_is_called_out()
    {
        var request = Request(new PlanQuotaInput(QuotaType.WhatsAppMessages, 10_000)) with
        {
            CountryPrices = new[] { new CountryPriceInput("DE", 50m) }
        };
        var report = await Service().BuildAsync(request);

        var germany = report.Countries.Single(c => c.CountryCode == "DE");
        Assert.True(germany.IsSold);
        Assert.Equal(50m, germany.PriceLocal);
        Assert.True(germany.MarginLocal < 0);
        Assert.Contains(report.Warnings, w => w.Contains("loses money") && w.Contains("Germany"));
        // Only Germany has a price, so the plan is not sold in the United States - and says so.
        Assert.False(report.Countries.Single(c => c.CountryCode == "US").IsSold);
        Assert.Contains(report.Warnings, w => w.Contains("Not sold"));
    }

    [Fact]
    public async Task Only_enabled_countries_are_in_the_report()
    {
        _store.Rows["Countries:Disabled:IN"] = "true";

        var report = await Service().BuildAsync(Request(new PlanQuotaInput(QuotaType.WhatsAppMessages, 1000)));

        Assert.DoesNotContain(report.Countries, c => c.CountryCode == "IN");
        Assert.Contains(report.Countries, c => c.CountryCode == "US");
    }

    [Fact]
    public async Task With_no_default_model_chosen_it_says_so_rather_than_quietly_showing_zero()
    {
        var report = await Service(aiDefault: null, leadDefault: null).BuildAsync(Request(
            new PlanQuotaInput(QuotaType.AiConversations, 100),
            new PlanQuotaInput(QuotaType.LeadCandidates, 10)));

        Assert.Equal(0m, report.Ai.TotalUsd);
        Assert.Contains(report.Warnings, w => w.Contains("No default AI model"));
        Assert.Contains(report.Warnings, w => w.Contains("No default lead discovery model"));
    }

    [Fact]
    public async Task Nothing_typed_can_make_a_cost_negative_and_a_plan_with_no_quota_costs_nothing()
    {
        var bad = Simple with { PromptTokensPerConversation = -500, MarketingSharePercent = -10 };
        var report = await Service().BuildAsync(new PlanCostReportRequest(
            new[] { new PlanQuotaInput(QuotaType.AiConversations, 100), new PlanQuotaInput(QuotaType.WhatsAppMessages, -5) }, Prices, bad));

        Assert.All(report.Countries, c => Assert.True(c.TotalCostUsd >= 0));

        var empty = await Service().BuildAsync(Request());
        Assert.All(empty.Countries, c => Assert.Equal(0m, c.TotalCostUsd));
    }

    [Fact]
    public async Task The_starting_assumptions_are_learned_from_real_usage_when_there_is_some()
    {
        var builtIn = await Service().GetDefaultsAsync();
        Assert.StartsWith("Built-in", builtIn.AiSource);
        Assert.StartsWith("Built-in", builtIn.LeadSource);

        // Only the usage figures matter here, not the conversations and profiles those rows would point at.
        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        var tenant = new Tenant { Name = "Acme", Slug = "acme" };
        _db.Tenants.Add(tenant);
        _db.AiInteractions.Add(new AiInteraction { TenantId = tenant.Id, CreatedAt = _clock.UtcNow, PromptTokens = 2000, CompletionTokens = 400, ModelUsed = "OpenAI:gpt-5-mini" });
        _db.AiInteractions.Add(new AiInteraction { TenantId = tenant.Id, CreatedAt = _clock.UtcNow, PromptTokens = 1000, CompletionTokens = 200, ModelUsed = "OpenAI:gpt-5-mini" });
        _db.LeadDiscoveryRuns.Add(new LeadDiscoveryRun
        {
            TenantId = tenant.Id, RanAtUtc = _clock.UtcNow, CandidatesConsidered = 10,
            InputTokens = 50_000, CacheReadTokens = 10_000, CacheWriteTokens = 0, OutputTokens = 8_000, WebSearches = 5
        });
        await _db.SaveChangesAsync();

        var learned = await Service().GetDefaultsAsync();

        Assert.Equal(1500, learned.Assumptions.PromptTokensPerConversation);
        Assert.Equal(300, learned.Assumptions.CompletionTokensPerConversation);
        Assert.Equal(6000, learned.Assumptions.InputTokensPerCandidate);
        Assert.Equal(800, learned.Assumptions.OutputTokensPerCandidate);
        Assert.Equal(0.5m, learned.Assumptions.WebSearchesPerCandidate);
        Assert.Contains("2 AI conversations", learned.AiSource);
        Assert.Contains("10 candidates", learned.LeadSource);
    }
}
