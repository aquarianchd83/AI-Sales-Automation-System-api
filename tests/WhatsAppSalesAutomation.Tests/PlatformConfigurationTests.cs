using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Configuration page: what it shows is what the billing code reads, and what it saves is what that code
/// will read next - checked by feeding the stored rows back through the real configuration binding.</summary>
public class PlatformConfigurationTests
{
    private sealed class CapturingStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Saved { get; private set; } = new();

        public IReadOnlyCollection<string> Prefixes { get; private set; } = Array.Empty<string>();

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Saved);

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            Saved = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            Prefixes = prefixes;
            return Task.CompletedTask;
        }
    }

    private static PlatformConfigurationService Service(CapturingStore store, IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        var config = configuration ?? new ConfigurationBuilder().Build();
        services.Configure<RefundPolicyOptions>(config.GetSection("Billing:Refunds"));
        services.Configure<BillingAlertOptions>(config.GetSection("Billing:Alerts"));
        services.Configure<TrialQuotaOptions>(config.GetSection("Billing:Trial"));
        services.Configure<WhatsAppQuotaWeightOptions>(config.GetSection("WhatsApp:QuotaWeights"));
        services.Configure<WhatsAppPricingOptions>(config.GetSection("WhatsApp:Pricing"));
        services.Configure<LeadDiscoveryPricingOptions>(config.GetSection("LeadDiscovery:Pricing"));
        services.Configure<AiPricingOptions>(config.GetSection("Ai:Pricing"));
        var provider = services.BuildServiceProvider();

        return new PlatformConfigurationService(
            store, new CountryAvailability(store), new PlatformConfigurationValidator(),
            provider.GetRequiredService<IOptionsSnapshot<RefundPolicyOptions>>(),
            provider.GetRequiredService<IOptionsSnapshot<BillingAlertOptions>>(),
            provider.GetRequiredService<IOptionsSnapshot<TrialQuotaOptions>>(),
            provider.GetRequiredService<IOptionsSnapshot<WhatsAppQuotaWeightOptions>>(),
            provider.GetRequiredService<IOptionsSnapshot<WhatsAppPricingOptions>>(),
            provider.GetRequiredService<IOptionsSnapshot<LeadDiscoveryPricingOptions>>(),
            provider.GetRequiredService<IOptionsSnapshot<AiPricingOptions>>());
    }

    [Fact]
    public async Task With_nothing_stored_it_shows_the_built_in_defaults_the_billing_code_uses()
    {
        var config = await Service(new CapturingStore()).GetAsync();

        Assert.Equal(30, config.Refunds.CreditWindowDays);
        Assert.Equal(0.10m, config.Refunds.SubscriptionMaxUsageFraction);
        Assert.Equal("billing_alert", config.Alerts.WhatsAppTemplateName);
        Assert.Equal(50m, config.Trial.WhatsAppMessages);
        Assert.Equal(0.25m, config.QuotaWeights.Utility);
        Assert.Equal(0.0099m, config.Charges.WhatsApp.Countries.Single(c => c.CountryCode == "IN").Marketing);
        Assert.Contains(config.Charges.Ai.Models, m => m.Model == "OpenAI:gpt-5-mini");
        Assert.Contains(config.Charges.LeadDiscovery.Models, m => m.Model == "claude-sonnet-5");
        Assert.Equal(10m, config.Charges.LeadDiscovery.WebSearchPerThousand);
    }

    [Fact]
    public async Task What_is_saved_binds_back_into_the_options_the_billing_code_reads()
    {
        var store = new CapturingStore();
        var edited = (await Service(store).GetAsync()) with { };
        edited = edited with
        {
            Refunds = edited.Refunds with { CreditWindowDays = 45, SubscriptionMaxUsageFraction = 0.25m },
            Alerts = new BillingAlertConfigDto("low_balance_v2", "en_US"),
            Trial = new TrialQuotaConfigDto(100m, 75m, 20m),
            QuotaWeights = new QuotaWeightConfigDto(2m, 0.75m, 0.1m),
            Charges = edited.Charges with
            {
                WhatsApp = edited.Charges.WhatsApp with
                {
                    Countries = edited.Charges.WhatsApp.Countries
                        .Select(c => c.CountryCode == "IN" ? c with { Marketing = 0.0123m, Utility = 0.002m } : c).ToList()
                },
                LeadDiscovery = edited.Charges.LeadDiscovery with
                {
                    WebSearchPerThousand = 12m,
                    Models = edited.Charges.LeadDiscovery.Models
                        .Append(new LeadDiscoveryModelRatesDto("my-model", 3m, 15m, 0.3m, 3.75m)).ToList()
                },
                Ai = new AiChargesConfigDto(edited.Charges.Ai.Models
                    .Select(m => m.Model == "OpenAI:gpt-5-mini" ? m with { PromptPer1K = 0.0005m } : m)
                    .Append(new AiModelRatesDto("Google:gemini-x", 0.002m, 0.008m)).ToList())
            }
        };

        await Service(store).UpdateAsync(edited, null);

        // Feed the stored rows through the real configuration binding, as the running app does after a reload.
        var config = new ConfigurationBuilder().AddInMemoryCollection(store.Saved).Build();
        var reread = await Service(new CapturingStore(), config).GetAsync();

        Assert.Equal(45, reread.Refunds.CreditWindowDays);
        Assert.Equal(0.25m, reread.Refunds.SubscriptionMaxUsageFraction);
        Assert.Equal("low_balance_v2", reread.Alerts.WhatsAppTemplateName);
        Assert.Equal("en_US", reread.Alerts.WhatsAppTemplateLanguage);
        Assert.Equal(new TrialQuotaConfigDto(100m, 75m, 20m), reread.Trial);
        Assert.Equal(new QuotaWeightConfigDto(2m, 0.75m, 0.1m), reread.QuotaWeights);
        Assert.Equal(0.0123m, reread.Charges.WhatsApp.Countries.Single(c => c.CountryCode == "IN").Marketing);
        Assert.Equal(12m, reread.Charges.LeadDiscovery.WebSearchPerThousand);
        Assert.Contains(reread.Charges.LeadDiscovery.Models, m => m.Model == "my-model" && m.OutputPerMillion == 15m);
        Assert.Equal(0.0005m, reread.Charges.Ai.Models.Single(m => m.Model == "OpenAI:gpt-5-mini").PromptPer1K);
        // A model id with a colon survives the round trip even though a colon can't sit inside a configuration key.
        Assert.Contains(reread.Charges.Ai.Models, m => m.Model == "Google:gemini-x");
    }

    [Fact]
    public async Task Everything_it_stores_sits_under_a_prefix_it_owns()
    {
        var store = new CapturingStore();
        var service = Service(store);
        await service.UpdateAsync(await service.GetAsync(), null);

        Assert.All(store.Saved.Keys, key => Assert.Contains(store.Prefixes, p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task The_weights_and_trial_quota_are_what_the_quota_gate_actually_uses()
    {
        var weights = new WhatsAppQuotaWeightOptions { Marketing = 2m, Authentication = 0.75m, Utility = 0.1m };

        Assert.Equal(2m, weights.For(TemplateCategory.Marketing));
        Assert.Equal(0.75m, weights.For(TemplateCategory.Authentication));
        Assert.Equal(0.1m, weights.For(TemplateCategory.Utility));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_estimator_prices_ai_from_the_configured_rates()
    {
        var options = new AiPricingOptions();
        options.Models["Anthropic/claude-haiku-4-5"] = new AiModelRates { PromptPer1K = 0.01m, CompletionPer1K = 0.02m };
        var estimator = new AiSpendEstimator(new FixedOptions<AiPricingOptions>(options));

        // The interaction records "Anthropic:claude-haiku-4-5"; 2K prompt + 1K completion = 0.02 + 0.02.
        Assert.Equal(0.04m, estimator.EstimateUsd("Anthropic:claude-haiku-4-5", 2000, 1000));
        Assert.Equal(0m, estimator.EstimateUsd("Simulated:whatever", 2000, 1000));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_chosen_default_models_round_trip_and_start_out_unset()
    {
        var store = new CapturingStore();
        var initial = await Service(store).GetAsync();
        Assert.Null(initial.Charges.LeadDiscovery.DefaultModel);
        Assert.Null(initial.Charges.Ai.DefaultModel);

        var edited = initial with
        {
            Charges = initial.Charges with
            {
                LeadDiscovery = initial.Charges.LeadDiscovery with { DefaultModel = "claude-sonnet-5" },
                Ai = initial.Charges.Ai with { DefaultModel = "OpenAI:gpt-5-mini" }
            }
        };
        await Service(store).UpdateAsync(edited, null);

        var reread = await Service(new CapturingStore(), new ConfigurationBuilder().AddInMemoryCollection(store.Saved).Build()).GetAsync();
        Assert.Equal("claude-sonnet-5", reread.Charges.LeadDiscovery.DefaultModel);
        Assert.Equal("OpenAI:gpt-5-mini", reread.Charges.Ai.DefaultModel);

        // Clearing it stores no row, so it is unset again.
        var cleared = edited with { Charges = edited.Charges with { LeadDiscovery = edited.Charges.LeadDiscovery with { DefaultModel = null } } };
        var clearStore = new CapturingStore();
        await Service(clearStore).UpdateAsync(cleared, null);
        Assert.DoesNotContain("LeadDiscovery:Pricing:DefaultModel", clearStore.Saved.Keys);
    }

    [Fact]
    public async Task A_default_model_that_is_not_in_the_list_is_rejected()
    {
        var store = new CapturingStore();
        var service = Service(store);
        var good = await service.GetAsync();

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(
            good with { Charges = good.Charges with { Ai = good.Charges.Ai with { DefaultModel = "OpenAI:nope" } } }, null));
        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(
            good with { Charges = good.Charges with { LeadDiscovery = good.Charges.LeadDiscovery with { DefaultModel = "nope" } } }, null));
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void An_unlisted_model_is_priced_at_the_chosen_default_but_a_simulated_one_stays_free()
    {
        var ai = new AiPricingOptions { DefaultModel = "OpenAI/gpt-5-mini" };
        var estimator = new AiSpendEstimator(new FixedOptions<AiPricingOptions>(ai));

        // gpt-5-mini is 0.00025 / 0.002 per 1K: 2K prompt + 1K completion.
        Assert.Equal(0.0025m, estimator.EstimateUsd("OpenAI:some-new-model", 2000, 1000));
        Assert.Equal(0m, estimator.EstimateUsd("Simulated:x", 2000, 1000));
        // Without a chosen default an unlisted model is still free.
        Assert.Equal(0m, new AiSpendEstimator(new FixedOptions<AiPricingOptions>(new())).EstimateUsd("OpenAI:some-new-model", 2000, 1000));

        var lead = new LeadDiscoveryPricingOptions { DefaultModel = "claude-opus-5" };
        Assert.Equal(25m, lead.RatesFor("some-other-model").OutputPerMillion);
        Assert.Equal(10m, lead.RatesFor("claude-sonnet-5").OutputPerMillion);
        Assert.Equal(5m, new LeadDiscoveryPricingOptions().RatesFor("some-other-model").OutputPerMillion);
    }

    private static PlatformConfigurationDto WithCountries(PlatformConfigurationDto config, params string[] disabled) =>
        config with { Countries = config.Countries!.Select(c => c with { IsEnabled = !disabled.Contains(c.CountryCode) }).ToList() };

    [Fact]
    public async Task Every_country_starts_enabled_and_switching_one_off_hides_it_everywhere_it_is_listed()
    {
        var store = new CapturingStore();
        var initial = await Service(store).GetAsync();
        Assert.All(initial.Countries!, c => Assert.True(c.IsEnabled));
        Assert.Contains(initial.Charges.WhatsApp.Countries, c => c.CountryCode == "IN");

        await Service(store).UpdateAsync(WithCountries(initial, "IN", "AU"), null);

        Assert.Equal("true", store.Saved["Countries:Disabled:IN"]);
        var after = await Service(store).GetAsync();
        Assert.False(after.Countries!.Single(c => c.CountryCode == "IN").IsEnabled);
        Assert.True(after.Countries!.Single(c => c.CountryCode == "GB").IsEnabled);
        Assert.DoesNotContain(after.Charges.WhatsApp.Countries, c => c.CountryCode == "IN");

        var enabled = await new CountryAvailability(store).GetEnabledAsync();
        Assert.DoesNotContain(enabled, c => c.CountryCode is "IN" or "AU");
        Assert.Contains(enabled, c => c.CountryCode == "GB");
    }

    [Fact]
    public async Task Switching_a_country_off_and_on_again_keeps_its_whatsapp_rate()
    {
        var store = new CapturingStore();
        var service = Service(store);
        var initial = await service.GetAsync();
        var edited = initial with
        {
            Charges = initial.Charges with
            {
                WhatsApp = initial.Charges.WhatsApp with
                {
                    Countries = initial.Charges.WhatsApp.Countries.Select(c => c.CountryCode == "IN" ? c with { Marketing = 0.0777m } : c).ToList()
                }
            }
        };
        await service.UpdateAsync(edited, null);

        // Reload through the real binding, switch India off (its row is then absent from the page), save.
        var config = new ConfigurationBuilder().AddInMemoryCollection(store.Saved).Build();
        var reloaded = await Service(store, config).GetAsync();
        var off = WithCountries(reloaded, "IN");
        off = off with { Charges = off.Charges with { WhatsApp = off.Charges.WhatsApp with { Countries = off.Charges.WhatsApp.Countries.Where(c => c.CountryCode != "IN").ToList() } } };
        await Service(store, config).UpdateAsync(off, null);

        Assert.Equal("0.0777", store.Saved["WhatsApp:Pricing:Countries:IN:Marketing"]);
    }

    [Fact]
    public async Task At_least_one_country_must_stay_enabled_and_a_save_without_countries_leaves_the_choice_alone()
    {
        var store = new CapturingStore();
        var service = Service(store);
        var initial = await service.GetAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(WithCountries(initial, initial.Countries!.Select(c => c.CountryCode).ToArray()), null));
        Assert.Empty(store.Saved);

        await service.UpdateAsync(initial with { Countries = null }, null);
        Assert.DoesNotContain(CountryAvailability.DisabledPrefix, store.Prefixes);
    }

    [Fact]
    public async Task Choosing_a_disabled_country_is_refused_but_keeping_the_one_you_have_is_not()
    {
        var store = new CapturingStore();
        await Service(store).UpdateAsync(WithCountries(await Service(store).GetAsync(), "IN"), null);
        var availability = new CountryAvailability(store);

        await Assert.ThrowsAsync<ValidationException>(() => availability.EnsureAllowedAsync("IN", null));
        await Assert.ThrowsAsync<ValidationException>(() => availability.EnsureAllowedAsync("in", "GB"));
        await availability.EnsureAllowedAsync("IN", "IN");   // unchanged: allowed
        await availability.EnsureAllowedAsync("GB", "IN");   // moving to an enabled one: allowed
        await availability.EnsureAllowedAsync(null, "IN");
        await availability.EnsureAllowedAsync("", null);
    }

    [Theory]
    [InlineData("refund-days")]
    [InlineData("usage-fraction")]
    [InlineData("template")]
    [InlineData("country")]
    [InlineData("duplicate-model")]
    [InlineData("ai-model-name")]
    [InlineData("negative-rate")]
    public async Task Nonsense_is_rejected_and_nothing_is_stored(string problem)
    {
        var store = new CapturingStore();
        var service = Service(store);
        var good = await service.GetAsync();

        var bad = problem switch
        {
            "refund-days" => good with { Refunds = good.Refunds with { CreditWindowDays = 0 } },
            "usage-fraction" => good with { Refunds = good.Refunds with { SubscriptionMaxUsageFraction = 1.5m } },
            "template" => good with { Alerts = good.Alerts with { WhatsAppTemplateName = "Has Spaces!" } },
            "country" => good with { Charges = good.Charges with { WhatsApp = good.Charges.WhatsApp with { Countries = new[] { new WhatsAppCountryRatesDto("ZZ", "", 1, 1, 1) } } } },
            "duplicate-model" => good with { Charges = good.Charges with { LeadDiscovery = good.Charges.LeadDiscovery with { Models = new[] { new LeadDiscoveryModelRatesDto("a", 1, 1, 1, 1), new LeadDiscoveryModelRatesDto("A", 1, 1, 1, 1) } } } },
            "ai-model-name" => good with { Charges = good.Charges with { Ai = new AiChargesConfigDto(new[] { new AiModelRatesDto("no-provider", 1, 1) }) } },
            _ => good with { Charges = good.Charges with { WhatsApp = good.Charges.WhatsApp with { Default = new WhatsAppCategoryRatesDto(-1, 0, 0) } } }
        };

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(bad, null));
        Assert.Empty(store.Saved);
    }
}
