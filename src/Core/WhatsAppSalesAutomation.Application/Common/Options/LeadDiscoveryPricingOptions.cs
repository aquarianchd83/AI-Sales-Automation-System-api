namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Per-million-token rates for one model, in USD.</summary>
public class LeadDiscoveryModelRates
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    /// <summary>Cached input read back - typically a tenth of the input rate.</summary>
    public decimal CacheReadPerMillion { get; set; }

    /// <summary>Writing input into the cache - typically a quarter dearer than plain input.</summary>
    public decimal CacheWritePerMillion { get; set; }
}

/// <summary>
/// Bound from "LeadDiscovery:Pricing". What a discovery run is charged at, used to turn the usage the API
/// reports into the money figure a tenant sees (see LeadDiscovery.LeadDiscoveryCost).
///
/// These are list prices copied by hand, the same "hand-maintained catalog, changes on the order of a deploy"
/// treatment as Billing.RegionalPricingCatalog - and the same caveat: they can lag a provider price change,
/// so every figure derived from them is an estimate and the platform's own Anthropic bill is the authority.
/// Re-check them when changing LeadDiscovery:Agent:Model.
/// </summary>
public class LeadDiscoveryPricingOptions
{
    /// <summary>Web search is billed per search on top of tokens. Page fetches are billed as input tokens,
    /// not per fetch, so they need no rate here.</summary>
    public decimal WebSearchPerThousand { get; set; } = 10m;

    /// <summary>Keyed by the model id in LeadDiscovery:Agent:Model. Configuration merges into these defaults
    /// by key, so a rate can be corrected without restating the whole table.</summary>
    public Dictionary<string, LeadDiscoveryModelRates> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-4-5-20251001"] = new() { InputPerMillion = 1m, OutputPerMillion = 5m, CacheReadPerMillion = 0.1m, CacheWritePerMillion = 1.25m },
        ["claude-haiku-4-5"] = new() { InputPerMillion = 1m, OutputPerMillion = 5m, CacheReadPerMillion = 0.1m, CacheWritePerMillion = 1.25m },
        ["claude-sonnet-5"] = new() { InputPerMillion = 2m, OutputPerMillion = 10m, CacheReadPerMillion = 0.2m, CacheWritePerMillion = 2.5m },
        ["claude-opus-5"] = new() { InputPerMillion = 5m, OutputPerMillion = 25m, CacheReadPerMillion = 0.5m, CacheWritePerMillion = 6.25m },
        // A simulated run reports no usage at all, so this only makes its zero cost explicit.
        ["Simulated"] = new()
    };

    /// <summary>Applied to a model that has no entry above - the cheapest real rates, so an unpriced model
    /// shows an understated cost rather than a free one. A model the platform actually runs belongs in
    /// <see cref="Models"/>.</summary>
    public LeadDiscoveryModelRates Default { get; set; } =
        new() { InputPerMillion = 1m, OutputPerMillion = 5m, CacheReadPerMillion = 0.1m, CacheWritePerMillion = 1.25m };

    /// <summary>The model the platform runs, chosen on the Configuration page. A run whose model has no row of its own
    /// is priced at this model's rates rather than at <see cref="Default"/>. Blank means no choice was made.</summary>
    public string? DefaultModel { get; set; }

    public LeadDiscoveryModelRates RatesFor(string? model)
    {
        if (model is not null && Models.TryGetValue(model, out var rates))
            return rates;

        return !string.IsNullOrWhiteSpace(DefaultModel) && Models.TryGetValue(DefaultModel, out var chosen) ? chosen : Default;
    }
}
