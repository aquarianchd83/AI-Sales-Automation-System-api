using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Application.Platform;

public interface IPlatformConfigurationService
{
    /// <summary>The values in force right now - stored overrides where the operator has set them, the built-in defaults otherwise.</summary>
    Task<PlatformConfigurationDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores the whole document. Takes effect on the next request - no restart.</summary>
    Task<PlatformConfigurationDto> UpdateAsync(PlatformConfigurationDto request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Configuration page's backend. The numbers themselves are the options classes the billing code already reads
/// (<see cref="RefundPolicyOptions"/>, <see cref="BillingAlertOptions"/>, <see cref="TrialQuotaOptions"/>,
/// and the pricing options for WhatsApp, lead discovery and AI) - this
/// stores edits as AppSettings rows, which the configuration pipeline lets win over appsettings.json, and reloads
/// so the next request sees them. A built-in default that is never edited keeps applying.
/// </summary>
public class PlatformConfigurationService : IPlatformConfigurationService
{
    private static readonly string[] OwnedPrefixes =
    {
        "Billing:Refunds:", "Billing:Alerts:", "Billing:Trial:",
        // Quota weights used to be edited here; they now follow the message prices. Kept so an old stored value is cleaned up on the next save.
        "WhatsApp:QuotaWeights:",
        "WhatsApp:Pricing:", "LeadDiscovery:Pricing:", "Ai:Pricing:"
    };

    private readonly IAppSettingsStore _store;
    private readonly ICountryAvailability _countryAvailability;
    private readonly IValidator<PlatformConfigurationDto> _validator;
    private readonly RefundPolicyOptions _refunds;
    private readonly BillingAlertOptions _alerts;
    private readonly TrialQuotaOptions _trial;
    private readonly WhatsAppPricingOptions _whatsAppPricing;
    private readonly LeadDiscoveryPricingOptions _leadPricing;
    private readonly AiPricingOptions _aiPricing;
    private readonly TaxOptions _tax;
    private readonly FxOptions _fx;
    private readonly CostAssumptionsOptions _costing;

    public PlatformConfigurationService(
        IAppSettingsStore store,
        ICountryAvailability countryAvailability,
        IValidator<PlatformConfigurationDto> validator,
        IOptionsSnapshot<RefundPolicyOptions> refunds,
        IOptionsSnapshot<BillingAlertOptions> alerts,
        IOptionsSnapshot<TrialQuotaOptions> trial,
        IOptionsSnapshot<WhatsAppPricingOptions> whatsAppPricing,
        IOptionsSnapshot<LeadDiscoveryPricingOptions> leadPricing,
        IOptionsSnapshot<AiPricingOptions> aiPricing,
        IOptionsSnapshot<TaxOptions> tax,
        IOptionsSnapshot<FxOptions> fx,
        IOptionsSnapshot<CostAssumptionsOptions> costing)
    {
        _costing = costing.Value;
        _tax = tax.Value;
        _fx = fx.Value;
        _store = store;
        _countryAvailability = countryAvailability;
        _validator = validator;
        _refunds = refunds.Value;
        _alerts = alerts.Value;
        _trial = trial.Value;
        _whatsAppPricing = whatsAppPricing.Value;
        _leadPricing = leadPricing.Value;
        _aiPricing = aiPricing.Value;
    }

    public async Task<PlatformConfigurationDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var disabled = await _countryAvailability.GetDisabledAsync(cancellationToken);
        var catalog = RegionalPricingCatalog.All
            .GroupBy(r => r.CountryCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.CountryName)
            .ToList();

        // One rate row per country that is switched on, whether or not it has its own rate yet (it shows the default's).
        var countries = catalog
            .Where(r => !disabled.Contains(r.CountryCode))
            .Select(r =>
            {
                var rates = _whatsAppPricing.RatesFor(r.CountryCode);
                return new WhatsAppCountryRatesDto(r.CountryCode, r.CountryName, rates.Marketing, rates.Utility, rates.Authentication);
            })
            .ToList();

        var dto = new PlatformConfigurationDto(
            new RefundPolicyConfigDto(
                _refunds.CreditWindowDays, _refunds.SubscriptionWindowDays, _refunds.SubscriptionMaxUsageFraction,
                _refunds.SubscriptionPeriodDays, _refunds.RequestExpiryDays),
            new BillingAlertConfigDto(_alerts.WhatsAppTemplateName, _alerts.WhatsAppTemplateLanguage),
            new TrialQuotaConfigDto(_trial.WhatsAppMessages, _trial.AiConversations, _trial.LeadCandidates),
            new ChargesConfigDto(
                new WhatsAppChargesConfigDto(
                    new WhatsAppCategoryRatesDto(_whatsAppPricing.Default.Marketing, _whatsAppPricing.Default.Utility, _whatsAppPricing.Default.Authentication),
                    countries),
                new LeadDiscoveryChargesConfigDto(
                    _leadPricing.WebSearchPerThousand,
                    ToDto("Default", _leadPricing.Default),
                    _leadPricing.Models.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => ToDto(m.Key, m.Value)).ToList(),
                    string.IsNullOrWhiteSpace(_leadPricing.DefaultModel) ? null : _leadPricing.DefaultModel),
                new AiChargesConfigDto(
                    _aiPricing.Models.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(m => new AiModelRatesDto(m.Key.Replace('/', ':'), m.Value.PromptPer1K, m.Value.CompletionPer1K)).ToList(),
                    string.IsNullOrWhiteSpace(_aiPricing.DefaultModel) ? null : _aiPricing.DefaultModel.Replace('/', ':'))),
            catalog.Select(r => new CountryConfigDto(r.CountryCode, r.CountryName, r.CurrencyCode, r.CurrencySymbol, !disabled.Contains(r.CountryCode))).ToList(),
            new TaxConfigDto(
                _tax.SupplierStateCode,
                catalog.Select(r =>
                {
                    var rule = _tax.Countries.GetValueOrDefault(r.CountryCode);
                    return new TaxCountryConfigDto(r.CountryCode, r.CountryName, rule?.Name ?? "Tax", rule?.RatePercent ?? 0m, rule?.SplitByState ?? false);
                }).ToList()),
            new FxConfigDto(_fx.InrPerUsd),
            new PlanCostAssumptionsDto(
                _costing.MarketingSharePercent, _costing.UtilitySharePercent, _costing.AuthenticationSharePercent,
                _costing.PromptTokensPerConversation, _costing.CompletionTokensPerConversation,
                _costing.InputTokensPerCandidate, _costing.OutputTokensPerCandidate, _costing.WebSearchesPerCandidate));

        return dto;
    }

    public async Task<PlatformConfigurationDto> UpdateAsync(PlatformConfigurationDto request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void Set(string key, object value) => values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);

        Set("Billing:Refunds:CreditWindowDays", request.Refunds.CreditWindowDays);
        Set("Billing:Refunds:SubscriptionWindowDays", request.Refunds.SubscriptionWindowDays);
        Set("Billing:Refunds:SubscriptionMaxUsageFraction", request.Refunds.SubscriptionMaxUsageFraction);
        Set("Billing:Refunds:SubscriptionPeriodDays", request.Refunds.SubscriptionPeriodDays);
        Set("Billing:Refunds:RequestExpiryDays", request.Refunds.RequestExpiryDays);

        Set("Billing:Alerts:WhatsAppTemplateName", request.Alerts.WhatsAppTemplateName.Trim());
        Set("Billing:Alerts:WhatsAppTemplateLanguage", request.Alerts.WhatsAppTemplateLanguage.Trim());

        Set("Billing:Trial:WhatsAppMessages", request.Trial.WhatsAppMessages);
        Set("Billing:Trial:AiConversations", request.Trial.AiConversations);
        Set("Billing:Trial:LeadCandidates", request.Trial.LeadCandidates);


        var whatsApp = request.Charges.WhatsApp;
        SetWhatsAppRates("WhatsApp:Pricing:Default", whatsApp.Default.Marketing, whatsApp.Default.Utility, whatsApp.Default.Authentication);
        foreach (var country in whatsApp.Countries)
            SetWhatsAppRates($"WhatsApp:Pricing:Countries:{country.CountryCode.ToUpperInvariant()}", country.Marketing, country.Utility, country.Authentication);

        // A country switched off isn't on the page, so its rate isn't in the request - keep what it has rather than
        // losing it, so switching it back on doesn't reset its price.
        var sent = whatsApp.Countries.Select(c => c.CountryCode.ToUpperInvariant()).ToHashSet();
        foreach (var kept in _whatsAppPricing.Countries.Where(c => !sent.Contains(c.Key.ToUpperInvariant())))
            SetWhatsAppRates($"WhatsApp:Pricing:Countries:{kept.Key.ToUpperInvariant()}", kept.Value.Marketing, kept.Value.Utility, kept.Value.Authentication);

        var prefixes = OwnedPrefixes.ToList();
        if (request.Countries is { } countryChoices)
        {
            prefixes.Add(CountryAvailability.DisabledPrefix);
            foreach (var choice in countryChoices.Where(c => !c.IsEnabled))
                values[$"{CountryAvailability.DisabledPrefix}{choice.CountryCode.ToUpperInvariant()}"] = "true";
        }

        if (request.Tax is { } taxConfig)
        {
            prefixes.Add("Tax:");
            Set("Tax:SupplierStateCode", taxConfig.SupplierStateCode.Trim().ToUpperInvariant());
            foreach (var country in taxConfig.Countries)
            {
                var key = $"Tax:Countries:{country.CountryCode.ToUpperInvariant()}";
                Set($"{key}:Name", country.TaxName.Trim());
                Set($"{key}:RatePercent", country.RatePercent);
                Set($"{key}:SplitByState", country.SplitByState);
            }
        }

        if (request.CostAssumptions is { } costing)
        {
            prefixes.Add("Costing:");
            Set("Costing:MarketingSharePercent", costing.MarketingSharePercent);
            Set("Costing:UtilitySharePercent", costing.UtilitySharePercent);
            Set("Costing:AuthenticationSharePercent", costing.AuthenticationSharePercent);
            Set("Costing:PromptTokensPerConversation", costing.PromptTokensPerConversation);
            Set("Costing:CompletionTokensPerConversation", costing.CompletionTokensPerConversation);
            Set("Costing:InputTokensPerCandidate", costing.InputTokensPerCandidate);
            Set("Costing:OutputTokensPerCandidate", costing.OutputTokensPerCandidate);
            Set("Costing:WebSearchesPerCandidate", costing.WebSearchesPerCandidate);
        }

        if (request.Fx is { } fxConfig)
        {
            prefixes.Add("Fx:");
            Set("Fx:InrPerUsd", fxConfig.InrPerUsd);
        }

        var lead = request.Charges.LeadDiscovery;
        Set("LeadDiscovery:Pricing:WebSearchPerThousand", lead.WebSearchPerThousand);
        SetLeadRates("LeadDiscovery:Pricing:Default", lead.Default);
        foreach (var model in lead.Models)
            SetLeadRates($"LeadDiscovery:Pricing:Models:{model.Model.Trim()}", model);

        if (!string.IsNullOrWhiteSpace(lead.DefaultModel))
            values["LeadDiscovery:Pricing:DefaultModel"] = lead.DefaultModel.Trim();

        if (!string.IsNullOrWhiteSpace(request.Charges.Ai.DefaultModel))
            values["Ai:Pricing:DefaultModel"] = AiPricingOptions.KeyFor(request.Charges.Ai.DefaultModel.Trim());

        foreach (var model in request.Charges.Ai.Models)
        {
            var key = $"Ai:Pricing:Models:{AiPricingOptions.KeyFor(model.Model.Trim())}";
            Set($"{key}:PromptPer1K", model.PromptPer1K);
            Set($"{key}:CompletionPer1K", model.CompletionPer1K);
        }

        await _store.ReplacePrefixesAsync(values, prefixes, updatedByUserId, cancellationToken);

        // The options this scope already bound are the old values, so the answer is built from what was just stored.
        return request with
        {
            Tax = request.Tax is null ? null : request.Tax with
            {
                SupplierStateCode = request.Tax.SupplierStateCode.Trim().ToUpperInvariant(),
                Countries = request.Tax.Countries.Select(c => c with { CountryCode = c.CountryCode.ToUpperInvariant(), CountryName = CountryName(c.CountryCode) }).ToList()
            },
            Countries = request.Countries?.Select(c => c with { CountryCode = c.CountryCode.ToUpperInvariant(), CountryName = CountryName(c.CountryCode) }).ToList(),
            Charges = request.Charges with
            {
                WhatsApp = whatsApp with
                {
                    Countries = whatsApp.Countries
                        .Select(c => c with { CountryCode = c.CountryCode.ToUpperInvariant(), CountryName = CountryName(c.CountryCode) })
                        .OrderBy(c => c.CountryName).ToList()
                }
            }
        };

        void SetWhatsAppRates(string prefix, decimal marketing, decimal utility, decimal authentication)
        {
            Set($"{prefix}:Marketing", marketing);
            Set($"{prefix}:Utility", utility);
            Set($"{prefix}:Authentication", authentication);
        }

        void SetLeadRates(string prefix, LeadDiscoveryModelRatesDto rates)
        {
            Set($"{prefix}:InputPerMillion", rates.InputPerMillion);
            Set($"{prefix}:OutputPerMillion", rates.OutputPerMillion);
            Set($"{prefix}:CacheReadPerMillion", rates.CacheReadPerMillion);
            Set($"{prefix}:CacheWritePerMillion", rates.CacheWritePerMillion);
        }
    }

    private static string CountryName(string code) =>
        RegionalPricingCatalog.All.FirstOrDefault(r => string.Equals(r.CountryCode, code, StringComparison.OrdinalIgnoreCase))?.CountryName ?? code;

    private static LeadDiscoveryModelRatesDto ToDto(string model, LeadDiscoveryModelRates r) =>
        new(model, r.InputPerMillion, r.OutputPerMillion, r.CacheReadPerMillion, r.CacheWritePerMillion);
}

public class PlatformConfigurationValidator : AbstractValidator<PlatformConfigurationDto>
{
    private const decimal MaxUnits = 1_000_000_000m;
    private static readonly Regex TemplateName = new("^[a-z0-9_]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex LanguageCode = new("^[A-Za-z]{2,3}([_-][A-Za-z0-9]{2,8})?$", RegexOptions.Compiled);
    private static readonly Regex LeadModelName = new(@"^[A-Za-z0-9._\-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex AiModelName = new(@"^[A-Za-z0-9._\-]+:[A-Za-z0-9._\-:]+$", RegexOptions.Compiled);

    public PlatformConfigurationValidator()
    {
        RuleFor(x => x.Refunds).NotNull().ChildRules(r =>
        {
            r.RuleFor(x => x.CreditWindowDays).InclusiveBetween(1, 3650);
            r.RuleFor(x => x.SubscriptionWindowDays).InclusiveBetween(1, 3650);
            r.RuleFor(x => x.SubscriptionMaxUsageFraction).InclusiveBetween(0m, 1m).WithMessage("Usage share must be between 0 and 1 (0.10 = 10%).");
            r.RuleFor(x => x.SubscriptionPeriodDays).InclusiveBetween(1, 366);
            r.RuleFor(x => x.RequestExpiryDays).InclusiveBetween(1, 365);
        });

        RuleFor(x => x.Alerts).NotNull().ChildRules(a =>
        {
            a.RuleFor(x => x.WhatsAppTemplateName).NotEmpty().Must(n => n is not null && TemplateName.IsMatch(n.Trim()))
                .WithMessage("Template name uses lowercase letters, digits and underscores only.");
            a.RuleFor(x => x.WhatsAppTemplateLanguage).NotEmpty().Must(l => l is not null && LanguageCode.IsMatch(l.Trim()))
                .WithMessage("Use a language code such as en or en_US.");
        });

        RuleFor(x => x.Trial).NotNull().ChildRules(t =>
        {
            t.RuleFor(x => x.WhatsAppMessages).InclusiveBetween(0m, MaxUnits);
            t.RuleFor(x => x.AiConversations).InclusiveBetween(0m, MaxUnits);
            t.RuleFor(x => x.LeadCandidates).InclusiveBetween(0m, MaxUnits);
        });


        RuleFor(x => x.Countries!).ChildRules(list =>
        {
            list.RuleForEach(x => x).ChildRules(c =>
                c.RuleFor(x => x.CountryCode).Must(code => code is not null && RegionalPricingCatalog.IsValidCode(code))
                    .WithMessage("Country is not one the platform prices for."));
            list.RuleFor(x => x)
                .Must(l => l.Select(c => c.CountryCode?.ToUpperInvariant()).Distinct().Count() == l.Count)
                .WithMessage("Each country can be listed only once.");
            list.RuleFor(x => x).Must(l => l.Any(c => c.IsEnabled)).WithMessage("At least one country must stay enabled.");
        }).When(x => x.Countries is not null);

        RuleFor(x => x.Tax!).ChildRules(t =>
        {
            t.RuleFor(x => x.SupplierStateCode).Must(IndianStates.IsValidCode).WithMessage("Choose one of the listed states.");
            t.RuleForEach(x => x.Countries).ChildRules(c =>
            {
                c.RuleFor(x => x.CountryCode).Must(code => code is not null && RegionalPricingCatalog.IsValidCode(code))
                    .WithMessage("Country is not one the platform prices for.");
                c.RuleFor(x => x.TaxName).NotEmpty().MaximumLength(20);
                c.RuleFor(x => x.RatePercent).InclusiveBetween(0m, 100m);
            });
            t.RuleFor(x => x.Countries)
                .Must(l => l.Select(c => c.CountryCode?.ToUpperInvariant()).Distinct().Count() == l.Count)
                .WithMessage("Each country can be listed only once.");
        }).When(x => x.Tax is not null);

        RuleFor(x => x.CostAssumptions!).ChildRules(a =>
        {
            a.RuleFor(x => x.MarketingSharePercent).InclusiveBetween(0m, 1000m);
            a.RuleFor(x => x.UtilitySharePercent).InclusiveBetween(0m, 1000m);
            a.RuleFor(x => x.AuthenticationSharePercent).InclusiveBetween(0m, 1000m);
            a.RuleFor(x => x.PromptTokensPerConversation).InclusiveBetween(0, 10_000_000);
            a.RuleFor(x => x.CompletionTokensPerConversation).InclusiveBetween(0, 10_000_000);
            a.RuleFor(x => x.InputTokensPerCandidate).InclusiveBetween(0, 10_000_000);
            a.RuleFor(x => x.OutputTokensPerCandidate).InclusiveBetween(0, 10_000_000);
            a.RuleFor(x => x.WebSearchesPerCandidate).InclusiveBetween(0m, 1000m);
            a.RuleFor(x => x).Must(x => x.MarketingSharePercent + x.UtilitySharePercent + x.AuthenticationSharePercent > 0)
                .WithMessage("At least one WhatsApp category needs a share.");
        }).When(x => x.CostAssumptions is not null);

        RuleFor(x => x.Fx!).ChildRules(f =>
            f.RuleFor(x => x.InrPerUsd).InclusiveBetween(1m, 1000m).WithMessage("Rupees per dollar must be between 1 and 1000."))
            .When(x => x.Fx is not null);

        RuleFor(x => x.Charges).NotNull().ChildRules(c =>
        {
            c.RuleFor(x => x.WhatsApp).NotNull().ChildRules(w =>
            {
                w.RuleFor(x => x.Default).NotNull().ChildRules(RatesRules);
                w.RuleForEach(x => x.Countries).ChildRules(country =>
                {
                    country.RuleFor(x => x.CountryCode).Must(code => code is not null && RegionalPricingCatalog.IsValidCode(code))
                        .WithMessage("Country is not one the platform prices for.");
                    country.RuleFor(x => x.Marketing).InclusiveBetween(0m, 100m);
                    country.RuleFor(x => x.Utility).InclusiveBetween(0m, 100m);
                    country.RuleFor(x => x.Authentication).InclusiveBetween(0m, 100m);
                });
                w.RuleFor(x => x.Countries)
                    .Must(list => list.Select(x => x.CountryCode?.ToUpperInvariant()).Distinct().Count() == list.Count)
                    .WithMessage("Each country can be listed only once.");
            });

            c.RuleFor(x => x.LeadDiscovery).NotNull().ChildRules(l =>
            {
                l.RuleFor(x => x.WebSearchPerThousand).InclusiveBetween(0m, 10_000m);
                l.RuleFor(x => x.Default).NotNull().ChildRules(LeadRatesRules);
                l.RuleForEach(x => x.Models).ChildRules(model =>
                {
                    model.RuleFor(x => x.Model).Must(n => n is not null && LeadModelName.IsMatch(n.Trim()))
                        .WithMessage("Model id uses letters, digits, dots, dashes and underscores only.");
                    LeadRatesRules(model);
                });
                l.RuleFor(x => x.Models)
                    .Must(list => list.Select(m => m.Model?.Trim().ToLowerInvariant()).Distinct().Count() == list.Count)
                    .WithMessage("Each model can be listed only once.");
                l.RuleFor(x => x)
                    .Must(lead => string.IsNullOrWhiteSpace(lead.DefaultModel)
                        || lead.Models.Any(m => string.Equals(m.Model?.Trim(), lead.DefaultModel.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .WithMessage("The default lead discovery model must be one of the listed models.");
            });

            c.RuleFor(x => x.Ai).NotNull().ChildRules(a =>
            {
                a.RuleForEach(x => x.Models).ChildRules(model =>
                {
                    model.RuleFor(x => x.Model).Must(n => n is not null && AiModelName.IsMatch(n.Trim()))
                        .WithMessage("Model reads Provider:model, e.g. OpenAI:gpt-5-mini.");
                    model.RuleFor(x => x.PromptPer1K).InclusiveBetween(0m, 1000m);
                    model.RuleFor(x => x.CompletionPer1K).InclusiveBetween(0m, 1000m);
                });
                a.RuleFor(x => x.Models)
                    .Must(list => list.Select(m => m.Model?.Trim().ToLowerInvariant()).Distinct().Count() == list.Count)
                    .WithMessage("Each model can be listed only once.");
                a.RuleFor(x => x)
                    .Must(ai => string.IsNullOrWhiteSpace(ai.DefaultModel)
                        || ai.Models.Any(m => string.Equals(m.Model?.Trim(), ai.DefaultModel.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .WithMessage("The default AI model must be one of the listed models.");
            });
        });
    }

    private static void RatesRules(InlineValidator<WhatsAppCategoryRatesDto> rates)
    {
        rates.RuleFor(x => x.Marketing).InclusiveBetween(0m, 100m);
        rates.RuleFor(x => x.Utility).InclusiveBetween(0m, 100m);
        rates.RuleFor(x => x.Authentication).InclusiveBetween(0m, 100m);
    }

    private static void LeadRatesRules(InlineValidator<LeadDiscoveryModelRatesDto> rates)
    {
        rates.RuleFor(x => x.InputPerMillion).InclusiveBetween(0m, 10_000m);
        rates.RuleFor(x => x.OutputPerMillion).InclusiveBetween(0m, 10_000m);
        rates.RuleFor(x => x.CacheReadPerMillion).InclusiveBetween(0m, 10_000m);
        rates.RuleFor(x => x.CacheWritePerMillion).InclusiveBetween(0m, 10_000m);
    }
}
