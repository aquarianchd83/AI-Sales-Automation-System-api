using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Service margins screen: for each of the three services, what it costs the platform (worked out), the credits it draws from a tenant,
/// and what that leaves for each credit pack - a credit being worth what the pack it was bought in sold it for.</summary>
public sealed class ServiceMarginTests : IDisposable
{
    private sealed class Store : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Rows);

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var key in Rows.Keys.Where(k => prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList())
                Rows.Remove(key);
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Store _store = new();
    private readonly TestClock _clock = new();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Tenant _tenant = new() { Name = "T", Slug = "t" };

    public ServiceMarginTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ServiceMarginService Service(decimal inrPerUsd = 100m)
    {
        var costs = new PlanCostReportService(
            _db, new CountryAvailability(_store), _clock,
            new FixedOptions<WhatsAppPricingOptions>(new()),
            new FixedOptions<LeadDiscoveryPricingOptions>(new() { DefaultModel = "claude-sonnet-5" }),
            new FixedOptions<AiPricingOptions>(new() { DefaultModel = "OpenAI/gpt-5-mini" }),
            new FixedOptions<FxOptions>(new() { InrPerUsd = inrPerUsd }),
            new FixedOptions<CostAssumptionsOptions>(new()));
        var credits = new CreditPayerService(_db, _clock, new QuotaLedgerService(_db, _clock));
        return new ServiceMarginService(
            _db, costs, credits, _clock, new FixedOptions<FxOptions>(new() { InrPerUsd = inrPerUsd }),
            new FixedOptions<ManualServiceCostOptions>(StoredCosts()), _store, new FixedOptions<WhatsAppPricingOptions>(new()));
    }

    // What the live configuration would hold: the typed costs, as the store last saved them.
    private ManualServiceCostOptions StoredCosts()
    {
        decimal? Read(string key) =>
            _store.Rows.TryGetValue("ServiceCost:" + key, out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        return new() { ContentStudioGenerationInr = Read(nameof(ManualServiceCostOptions.ContentStudioGenerationInr)), ContentStudioVideoRenderInr = Read(nameof(ManualServiceCostOptions.ContentStudioVideoRenderInr)) };
    }

    private async Task<CreditPack> AddPackAsync(string name, decimal credits, decimal priceInr, bool active = true, string country = "IN", QuotaType type = QuotaType.Credits)
    {
        var pack = new CreditPack { Name = name, Units = credits, QuotaType = type, IsActive = active };
        _db.CreditPacks.Add(pack);
        _db.CreditPackPrices.Add(new CreditPackPrice { CreditPackId = pack.Id, CountryCode = country, Amount = priceInr });
        await _db.SaveChangesAsync();
        return pack;
    }

    private async Task AddReplyAsync(decimal? costUsd, AiExecutionMode mode = AiExecutionMode.Live, AiExecutionStatus status = AiExecutionStatus.Succeeded, int daysAgo = 1)
    {
        _db.AiExecutions.Add(new AiExecution
        {
            Task = AiTaskKinds.ConversationReply, Provider = "OpenAI", RequestedProvider = "OpenAI", Mode = mode, Status = status,
            EstimatedCostUsd = costUsd, CreatedAt = _clock.UtcNow.AddDays(-daysAgo)
        });
        await _db.SaveChangesAsync();
    }

    private static ServiceMarginRowDto Row(ServiceMarginDto dto, CreditScenario scenario) => dto.Rows.Single(r => r.Scenario == scenario);

    [Fact]
    public async Task Every_service_credits_are_spent_on_is_listed_in_the_order_of_the_credit_costs_screen()
    {
        var dto = await Service().GetAsync();

        Assert.Equal(CreditScenarioCatalog.All.Select(i => i.Scenario), dto.Rows.Select(r => r.Scenario));
        Assert.Equal(7, dto.Rows.Count);
        Assert.Equal(1m, Row(dto, CreditScenario.CustomerChatReply).CreditsCharged);
        Assert.Equal("Product knowledge: reading sources", Row(dto, CreditScenario.ProductKnowledgeReading).Title);
        Assert.Equal("action", Row(dto, CreditScenario.ProductKnowledgeAnswering).UnitLabel);
    }

    [Fact]
    public async Task The_three_estimated_services_have_a_cost_before_any_call_is_made()
    {
        var dto = await Service().GetAsync();

        foreach (var scenario in new[] { CreditScenario.CustomerChatReply, CreditScenario.WhatsAppTemplateSend, CreditScenario.LeadDiscovery })
            Assert.True(Row(dto, scenario).CostInr > 0m, scenario.ToString());
    }

    [Fact]
    public async Task A_whatsapp_message_is_a_marketing_message_and_draws_exactly_the_credits_tenants_are_shown()
    {
        var pack = await AddPackAsync("100 credits", 100m, 200m); // Rs 2 a credit
        await Service(inrPerUsd: 100m).UpdateAsync(
            new UpdateServiceMarginRequest(new[] { new ServiceMarginChargeInput(CreditScenario.WhatsAppTemplateSend, 2m) }), _admin);

        var wa = Row(await Service(inrPerUsd: 100m).GetAsync(), CreditScenario.WhatsAppTemplateSend);

        // The setting is what the tenant's Credit Cap page shows (2 credits per message); the box and the charge use that same number.
        Assert.Equal(2m, wa.CreditsPerUnit);
        Assert.Equal(2m, wa.CreditsCharged);
        Assert.Equal(1m, wa.UnitsPerItem);
        Assert.Equal("marketing message", wa.UnitLabel);
        Assert.Equal(4m, wa.ByPack.Single(p => p.PackId == pack.Id).ChargeInr);

        // India's marketing rate: $0.0099 a message, Rs 0.99 at 100 a dollar.
        Assert.Equal(0.99m, wa.CostInr);
    }

    [Fact]
    public async Task Content_studio_has_a_charge_but_no_cost_and_so_no_margin()
    {
        await AddPackAsync("100 credits", 100m, 200m);

        var dto = await Service().GetAsync();

        foreach (var scenario in new[] { CreditScenario.ContentStudioGeneration, CreditScenario.ContentStudioVideoRender })
        {
            var row = Row(dto, scenario);
            Assert.Null(row.CostInr);
            Assert.StartsWith("Not tracked", row.CostBasis);
            var onPack = Assert.Single(row.ByPack);
            Assert.True(onPack.ChargeInr > 0m);
            Assert.Null(onPack.MarginInr);
            Assert.Null(onPack.MarginPercent);
        }

        // A video render draws ten credits by default, at Rs 2 a credit.
        Assert.Equal(20m, Row(dto, CreditScenario.ContentStudioVideoRender).ByPack.Single().ChargeInr);
    }

    [Fact]
    public async Task Only_content_studio_rows_offer_a_cost_to_type()
    {
        var dto = await Service().GetAsync();

        Assert.Equal(
            new[] { CreditScenario.ContentStudioGeneration, CreditScenario.ContentStudioVideoRender },
            dto.Rows.Where(r => r.CostEditable).Select(r => r.Scenario));
        Assert.All(dto.Rows, r => Assert.False(r.CostIsManual));
    }

    [Fact]
    public async Task A_cost_typed_for_content_studio_gives_it_a_margin_on_every_pack()
    {
        var pack = await AddPackAsync("100 credits", 100m, 200m); // Rs 2 a credit

        // A video render draws 10 credits: Rs 20 charged against a typed Rs 8 cost.
        var dto = await Service().UpdateAsync(new UpdateServiceMarginRequest(
            Array.Empty<ServiceMarginChargeInput>(),
            new[] { new ServiceMarginManualCostInput(CreditScenario.ContentStudioVideoRender, 8m) }), _admin);

        var video = Row(dto, CreditScenario.ContentStudioVideoRender);
        Assert.Equal(8m, video.CostInr);
        Assert.True(video.CostIsManual);
        Assert.StartsWith("Typed in by you", video.CostBasis);
        var onPack = video.ByPack.Single(p => p.PackId == pack.Id);
        Assert.Equal(12m, onPack.MarginInr);
        Assert.Equal(60m, onPack.MarginPercent);

        // The other Content Studio row had none typed, so it still has no margin.
        Assert.Null(Row(dto, CreditScenario.ContentStudioGeneration).CostInr);
        Assert.Equal("8", _store.Rows["ServiceCost:ContentStudioVideoRenderInr"]);
    }

    [Fact]
    public async Task A_typed_cost_is_remembered_and_can_be_cleared()
    {
        await Service().UpdateAsync(new UpdateServiceMarginRequest(
            Array.Empty<ServiceMarginChargeInput>(),
            new[]
            {
                new ServiceMarginManualCostInput(CreditScenario.ContentStudioGeneration, 1.5m),
                new ServiceMarginManualCostInput(CreditScenario.ContentStudioVideoRender, 8m)
            }), _admin);

        // Clearing one leaves the other.
        var dto = await Service().UpdateAsync(new UpdateServiceMarginRequest(
            Array.Empty<ServiceMarginChargeInput>(),
            new[] { new ServiceMarginManualCostInput(CreditScenario.ContentStudioVideoRender, null) }), _admin);

        Assert.Null(Row(dto, CreditScenario.ContentStudioVideoRender).CostInr);
        Assert.False(Row(dto, CreditScenario.ContentStudioVideoRender).CostIsManual);
        Assert.False(_store.Rows.ContainsKey("ServiceCost:ContentStudioVideoRenderInr"));
        Assert.Equal(1.5m, Row(dto, CreditScenario.ContentStudioGeneration).CostInr);
    }

    [Theory]
    [InlineData(CreditScenario.CustomerChatReply)]
    [InlineData(CreditScenario.ProductKnowledgeReading)]
    public async Task A_cost_that_is_worked_out_for_you_cannot_be_typed_over(CreditScenario scenario)
    {
        var request = new UpdateServiceMarginRequest(Array.Empty<ServiceMarginChargeInput>(), new[] { new ServiceMarginManualCostInput(scenario, 1m) });

        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateAsync(request, _admin));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task A_negative_typed_cost_is_refused()
    {
        var request = new UpdateServiceMarginRequest(
            Array.Empty<ServiceMarginChargeInput>(), new[] { new ServiceMarginManualCostInput(CreditScenario.ContentStudioGeneration, -1m) });

        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateAsync(request, _admin));
    }

    [Fact]
    public async Task A_product_knowledge_action_costs_its_ai_calls_over_the_actions_charged()
    {
        // Two calls of Rs 1 each (at 100 a dollar), made for one charged action: the action cost Rs 2.
        foreach (var _ in new[] { 1, 2 })
            _db.AiExecutions.Add(new AiExecution
            {
                Task = AiTaskKinds.KnowledgeDraft, Provider = "OpenAI", RequestedProvider = "OpenAI", Mode = AiExecutionMode.Live,
                Status = AiExecutionStatus.Succeeded, EstimatedCostUsd = 0.01m, CreatedAt = _clock.UtcNow.AddDays(-1)
            });
        _db.CreditUsages.Add(new CreditUsage
        {
            TenantId = _tenant.Id, Scenario = CreditScenario.ProductKnowledgeAnswering, QuotaType = QuotaType.Credits, Units = 1m,
            ChargedTo = CreditPayer.Tenant, ConfiguredPayer = CreditPayer.Tenant, Status = CreditUsageStatus.Charged,
            OperationKey = "pk-1", OccurredAtUtc = _clock.UtcNow.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        var dto = await Service(inrPerUsd: 100m).GetAsync();

        var answering = Row(dto, CreditScenario.ProductKnowledgeAnswering);
        Assert.Equal(2m, answering.CostInr);
        Assert.Equal(1, answering.Samples);
        Assert.Contains("2 AI calls over 1 charged actions", answering.CostBasis);

        // Nothing was recorded for reading sources, so there is no cost to work from.
        var reading = Row(dto, CreditScenario.ProductKnowledgeReading);
        Assert.Null(reading.CostInr);
        Assert.Contains("nothing to work a cost from", reading.CostBasis);
    }

    [Fact]
    public async Task Without_charged_actions_a_product_knowledge_action_costs_the_average_call()
    {
        _db.AiExecutions.Add(new AiExecution
        {
            Task = AiTaskKinds.DocumentAnalysis, Provider = "OpenAI", RequestedProvider = "OpenAI", Mode = AiExecutionMode.Live,
            Status = AiExecutionStatus.Succeeded, EstimatedCostUsd = 0.03m, CreatedAt = _clock.UtcNow.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        var reading = Row(await Service(inrPerUsd: 100m).GetAsync(), CreditScenario.ProductKnowledgeReading);

        Assert.Equal(3m, reading.CostInr);
        Assert.Contains("Average of 1 AI calls", reading.CostBasis);
    }

    [Fact]
    public async Task Each_pack_gives_a_credit_its_own_rupee_value_and_so_its_own_margin()
    {
        await AddReplyAsync(0.01m); // Rs 1 at 100 a dollar
        var small = await AddPackAsync("100 credits", 100m, 249m);
        var big = await AddPackAsync("500 credits", 500m, 996m);

        var dto = await Service(inrPerUsd: 100m).GetAsync();

        // Dearest credit first.
        Assert.Equal(new[] { small.Id, big.Id }, dto.Packs.Select(p => p.Id));
        Assert.Equal(2.49m, dto.Packs[0].InrPerCredit);
        Assert.Equal(1.992m, dto.Packs[1].InrPerCredit);

        var ai = Row(dto, CreditScenario.CustomerChatReply);
        Assert.Equal(1m, ai.CostInr);
        var onSmall = ai.ByPack.Single(p => p.PackId == small.Id);
        Assert.Equal(2.49m, onSmall.ChargeInr);
        Assert.Equal(1.49m, onSmall.MarginInr);
        Assert.Equal(59.8m, onSmall.MarginPercent);
        var onBig = ai.ByPack.Single(p => p.PackId == big.Id);
        Assert.Equal(1.992m, onBig.ChargeInr);
        Assert.Equal(49.8m, onBig.MarginPercent);
    }

    [Fact]
    public async Task Only_active_credit_packs_with_an_india_price_are_measured()
    {
        var live = await AddPackAsync("Live", 100m, 249m);
        await AddPackAsync("Retired", 100m, 249m, active: false);
        await AddPackAsync("Abroad only", 100m, 5m, country: "US");
        await AddPackAsync("Unpriced", 100m, 0m);
        await AddPackAsync("Other quota", 100m, 249m, type: QuotaType.AiConversations);

        var dto = await Service().GetAsync();

        Assert.Equal(new[] { live.Id }, dto.Packs.Select(p => p.Id));
    }

    [Fact]
    public async Task With_no_pack_to_measure_against_it_says_so()
    {
        var dto = await Service().GetAsync();

        Assert.Empty(dto.Packs);
        Assert.All(dto.Rows, r => Assert.Empty(r.ByPack));
        Assert.Contains(dto.Notes, n => n.Contains("credit pack"));
    }

    [Fact]
    public async Task A_service_that_draws_more_credits_charges_more_on_every_pack()
    {
        var pack = await AddPackAsync("100 credits", 100m, 200m); // Rs 2 a credit
        await Service().UpdateAsync(new UpdateServiceMarginRequest(new[] { new ServiceMarginChargeInput(CreditScenario.CustomerChatReply, 3m) }), _admin);

        var ai = Row(await Service().GetAsync(), CreditScenario.CustomerChatReply);

        Assert.Equal(3m, ai.CreditsPerUnit);
        Assert.Equal(6m, ai.ByPack.Single(p => p.PackId == pack.Id).ChargeInr);
    }

    [Fact]
    public async Task With_no_pack_the_cost_estimate_comes_from_the_price_list()
    {
        var ai = Row(await Service().GetAsync(), CreditScenario.CustomerChatReply);

        Assert.StartsWith("Estimate", ai.CostBasis);
        Assert.Equal(0, ai.Samples);
    }

    [Fact]
    public async Task An_ai_reply_costs_the_average_of_the_real_replies_in_the_last_30_days()
    {
        await AddReplyAsync(0.002m);
        await AddReplyAsync(0.004m);

        // None of these is a real, paid reply: free (a price missing at the time), simulated, failed, or older than the window.
        await AddReplyAsync(0m);
        await AddReplyAsync(null);
        await AddReplyAsync(0.5m, mode: AiExecutionMode.Simulator);
        await AddReplyAsync(0.5m, status: AiExecutionStatus.Failed);
        await AddReplyAsync(0.5m, daysAgo: 45);

        var ai = Row(await Service(inrPerUsd: 100m).GetAsync(), CreditScenario.CustomerChatReply);

        Assert.Equal(2, ai.Samples);
        Assert.Equal(0.003m, ai.CostUsd);
        Assert.Equal(0.3m, ai.CostInr);
        Assert.Contains("2 AI replies", ai.CostBasis);
    }

    [Fact]
    public async Task A_candidate_costs_what_the_runs_cost_over_the_candidates_they_looked_at()
    {
        _db.LeadDiscoveryRuns.Add(new LeadDiscoveryRun { TenantId = _tenant.Id, RanAtUtc = _clock.UtcNow.AddDays(-2), CandidatesConsidered = 10, EstimatedCostUsd = 0.30m });
        _db.LeadDiscoveryRuns.Add(new LeadDiscoveryRun { TenantId = _tenant.Id, RanAtUtc = _clock.UtcNow.AddDays(-3), CandidatesConsidered = 30, EstimatedCostUsd = 0.90m });
        await _db.SaveChangesAsync();

        var leads = Row(await Service().GetAsync(), CreditScenario.LeadDiscovery);

        Assert.Equal(40, leads.Samples);
        Assert.Equal(0.03m, leads.CostUsd);
    }

    [Fact]
    public async Task A_cost_above_the_charge_is_a_negative_margin_on_that_pack()
    {
        await AddReplyAsync(0.05m); // Rs 5 at 100 a dollar
        var pack = await AddPackAsync("100 credits", 200m, 400m); // Rs 2 a credit

        var onPack = Row(await Service(inrPerUsd: 100m).GetAsync(), CreditScenario.CustomerChatReply).ByPack.Single(p => p.PackId == pack.Id);

        Assert.Equal(-3m, onPack.MarginInr);
        Assert.Equal(-150m, onPack.MarginPercent);
    }

    [Fact]
    public async Task A_service_that_draws_no_credits_has_no_margin_percent()
    {
        var pack = await AddPackAsync("100 credits", 100m, 249m);

        var dto = await Service().UpdateAsync(new UpdateServiceMarginRequest(new[] { new ServiceMarginChargeInput(CreditScenario.LeadDiscovery, 0m) }), _admin);

        var onPack = Row(dto, CreditScenario.LeadDiscovery).ByPack.Single(p => p.PackId == pack.Id);
        Assert.Equal(0m, onPack.ChargeInr);
        Assert.Null(onPack.MarginPercent);
        Assert.True(onPack.MarginInr < 0m);
    }

    [Fact]
    public async Task A_negative_number_of_credits_is_refused()
    {
        var request = new UpdateServiceMarginRequest(new[] { new ServiceMarginChargeInput(CreditScenario.CustomerChatReply, -1m) });

        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateAsync(request, _admin));
    }

    [Fact]
    public async Task Something_credits_are_not_spent_on_cannot_be_priced()
    {
        var request = new UpdateServiceMarginRequest(new[] { new ServiceMarginChargeInput((CreditScenario)99, 5m) });

        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateAsync(request, _admin));
    }

    [Fact]
    public async Task A_content_studio_or_product_knowledge_price_can_be_changed_here_too()
    {
        var dto = await Service().UpdateAsync(new UpdateServiceMarginRequest(new[]
        {
            new ServiceMarginChargeInput(CreditScenario.ContentStudioVideoRender, 15m),
            new ServiceMarginChargeInput(CreditScenario.ProductKnowledgeReading, 8m)
        }), _admin);

        Assert.Equal(15m, Row(dto, CreditScenario.ContentStudioVideoRender).CreditsPerUnit);
        Assert.Equal(8m, Row(dto, CreditScenario.ProductKnowledgeReading).CreditsPerUnit);
    }

    [Fact]
    public async Task A_service_the_platform_pays_for_is_marked()
    {
        _db.CreditScenarioSettings.Add(new CreditScenarioSetting { Scenario = CreditScenario.LeadDiscovery, Payer = CreditPayer.Platform });
        await _db.SaveChangesAsync();

        var dto = await Service().GetAsync();

        Assert.True(Row(dto, CreditScenario.LeadDiscovery).PaidByPlatform);
        Assert.False(Row(dto, CreditScenario.CustomerChatReply).PaidByPlatform);
    }
}
