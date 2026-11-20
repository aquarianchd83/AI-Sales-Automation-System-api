using FluentValidation;
using ValidationFailure = FluentValidation.Results.ValidationFailure;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One model the operator can pick for a group on one provider, with the price on the AI Provider Charges page (null when it has none).</summary>
public record AiTaskModelOptionDto(string Model, bool HasPrice, decimal? InputPer1M, decimal? OutputPer1M);

/// <summary>One provider's column for a group: what it runs on today and what can be chosen instead.</summary>
/// <param name="Available">The provider is switched on and has a key, so a choice made for it can take effect.</param>
/// <param name="ChatModel">The model the group runs on when nothing is chosen: the provider's chat model, or the discovery default for lead discovery.</param>
/// <param name="Selected">The model chosen for this group, or null for "Same as chat model".</param>
/// <param name="EffectiveModel">The model the group really runs on: the choice, else the chat model.</param>
public record AiTaskModelCellDto(
    string Provider,
    string ProviderLabel,
    bool Available,
    string ChatModel,
    string? Selected,
    string EffectiveModel,
    bool HasPrice,
    decimal? InputPer1M,
    decimal? OutputPer1M,
    IReadOnlyList<AiTaskModelOptionDto> Options);

/// <param name="DefaultLabel">What "nothing chosen" is called: "Same as chat model", or "Default" for lead discovery.</param>
/// <param name="AllowDefault">False when the group always has a model saved (lead discovery), so a blank cannot be chosen.</param>
public record AiTaskModelGroupDto(
    string Key, string Title, string Description, IReadOnlyList<string> Tasks, string DefaultLabel, bool AllowDefault, IReadOnlyList<AiTaskModelCellDto> Providers);

public record AiTaskModelsDto(IReadOnlyList<AiTaskModelGroupDto> Groups);

/// <param name="Model">The model to use for the group on that provider; null or blank means the provider's chat model.</param>
public record AiTaskModelChoiceRequest(string Group, string Provider, string? Model);

public record UpdateAiTaskModelsRequest(IReadOnlyList<AiTaskModelChoiceRequest> Choices);

public interface IPlatformAiTaskModelsService
{
    Task<AiTaskModelsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the choices (only the ones in the request) and returns the page as it now stands. Applies from the next request.</summary>
    Task<AiTaskModelsDto> UpdateAsync(UpdateAiTaskModelsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// "Model by task" on the AI Providers page. Each group of tasks (<see cref="AiModelGroups"/>) can run on its own model per provider; nothing chosen
/// means that provider's chat model, so a platform that never opens this card behaves exactly as before. The prices shown are the ones on the AI
/// Provider Charges page, and a model without one is flagged so a cost is never silently counted as free.
/// </summary>
public class PlatformAiTaskModelsService : IPlatformAiTaskModelsService
{
    private static readonly (string Provider, string Label)[] Providers =
    {
        ("Anthropic", "Claude"), ("OpenAI", "ChatGPT"), ("Google", "Gemini")
    };

    private const int MaxModelLength = 100;

    private readonly IAppSettingsStore _store;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly IOptionsSnapshot<AiPricingOptions> _pricing;
    private readonly IOptionsSnapshot<LeadDiscoveryPricingOptions> _leadPricing;
    private readonly IProviderModelCatalog _catalog;

    public PlatformAiTaskModelsService(
        IAppSettingsStore store, IPlatformAiConfigProvider credentials, IOptionsSnapshot<AiPricingOptions> pricing,
        IOptionsSnapshot<LeadDiscoveryPricingOptions> leadPricing, IProviderModelCatalog catalog)
    {
        _store = store;
        _credentials = credentials;
        _pricing = pricing;
        _leadPricing = leadPricing;
        _catalog = catalog;
    }

    public async Task<AiTaskModelsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return await BuildAsync(stored, cancellationToken);
    }

    public async Task<AiTaskModelsDto> UpdateAsync(UpdateAiTaskModelsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationFailure>();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var choice in request.Choices ?? Array.Empty<AiTaskModelChoiceRequest>())
        {
            var group = AiModelGroups.Canonical(choice.Group);
            var provider = Providers.FirstOrDefault(p => string.Equals(p.Provider, choice.Provider?.Trim(), StringComparison.OrdinalIgnoreCase)).Provider;
            var model = choice.Model?.Trim() ?? string.Empty;

            if (group is null)
                errors.Add(new ValidationFailure("Group", $"'{choice.Group}' is not a task group."));
            else if (provider is null)
                errors.Add(new ValidationFailure($"{group}.Provider", $"'{choice.Provider}' is not a provider. Choose ChatGPT, Claude or Gemini."));
            else if (model.Length == 0 && !AiModelGroups.All.First(g => g.Key == group).AllowDefault)
                errors.Add(new ValidationFailure($"{group}.{provider}", $"Choose a model for {AiModelGroups.All.First(g => g.Key == group).Title}."));
            else if (model.Length > MaxModelLength || model.Any(char.IsWhiteSpace))
                errors.Add(new ValidationFailure($"{group}.{provider}", "A model name has no spaces and is at most 100 characters."));
            else
                values[AiModelGroups.SettingKey(group, provider)] = model;
        }

        if (errors.Count > 0)
            throw new ValidationException(errors);

        if (values.Count > 0)
            await _store.UpsertAsync(values, updatedByUserId, cancellationToken);

        return await GetAsync(cancellationToken);
    }

    private async Task<AiTaskModelsDto> BuildAsync(IReadOnlyDictionary<string, string?> stored, CancellationToken cancellationToken)
    {
        var credentials = _credentials.Get();

        // Every active chat model the platform's catalogue (AI Provider Models) holds for each provider. One at a time: they share one database context.
        var listed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in Providers)
            listed[p.Provider] = await _catalog.ListAsync(p.Provider, cancellationToken);
        var pricing = _pricing.Value;
        var leadPricing = _leadPricing.Value;

        return new AiTaskModelsDto(AiModelGroups.All.Select(group => new AiTaskModelGroupDto(
            group.Key, group.Title, group.Description, group.Tasks, group.DefaultLabel, group.AllowDefault,
            Providers.Select(p => Cell(group, p.Provider, p.Label, credentials, stored, pricing, leadPricing, listed[p.Provider])).ToList())).ToList());
    }

    private AiTaskModelCellDto Cell(
        AiModelGroup group, string provider, string label, AiCredentials credentials, IReadOnlyDictionary<string, string?> stored, AiPricingOptions pricing,
        LeadDiscoveryPricingOptions leadPricing, IReadOnlyList<string> listed)
    {
        // Lead discovery is priced on its own table (LeadDiscovery:Pricing) and defaults to the discovery model, not the chat model.
        var discovery = group.Key == AiModelGroups.LeadDiscovery;
        // Lead discovery has no default model: nothing saved means nothing chosen (an empty name), and its run is skipped until one is.
        var chat = discovery ? string.Empty : ChatModelOf(credentials, provider);
        stored.TryGetValue(AiModelGroups.SettingKey(group.Key, provider), out var raw);
        var selected = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        var effective = selected ?? chat;

        var priced = discovery
            ? leadPricing.Models
                .Where(m => PlatformProviderOf(m.Key) == provider)
                .Select(m => new AiTaskModelOptionDto(m.Key, true, m.Value.InputPerMillion, m.Value.OutputPerMillion))
                .ToList()
            : pricing.Models
                .Where(m => m.Key.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase))
                .Select(m => new AiTaskModelOptionDto(m.Key[(provider.Length + 1)..], true, PerMillion(m.Value.PromptPer1K), PerMillion(m.Value.CompletionPer1K)))
                .ToList();

        // The models the operator can choose from: every model the catalogue lists for the provider, the priced ones, and whatever is chosen or in use now, so a current
        // choice is never missing.
        foreach (var model in listed.Concat(new[] { selected, chat }).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!))
        {
            if (!priced.Any(o => string.Equals(o.Model, model, StringComparison.OrdinalIgnoreCase)))
                priced.Add(discovery ? LeadPriceOf(leadPricing, model) : PriceOf(pricing, provider, model));
        }

        var current = discovery ? LeadPriceOf(leadPricing, effective) : PriceOf(pricing, provider, effective);
        return new AiTaskModelCellDto(
            provider, label, credentials.IsEnabled(provider) && !string.IsNullOrWhiteSpace(KeyOf(credentials, provider)),
            chat, selected, effective, current.HasPrice, current.InputPer1M, current.OutputPer1M,
            // Cheapest first (input plus output per million tokens); a model with no price goes last, by name.
            priced.OrderBy(o => o.HasPrice ? 0 : 1).ThenBy(o => (o.InputPer1M ?? 0m) + (o.OutputPer1M ?? 0m)).ThenBy(o => o.Model, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The provider a discovery model belongs to, read from its name (a discovery price row is keyed by model alone).</summary>
    private static string? PlatformProviderOf(string model) => LeadDiscovery.LeadDiscoveryHistoryService.ProviderOf(model);

    private static AiTaskModelOptionDto LeadPriceOf(LeadDiscoveryPricingOptions pricing, string model)
    {
        // A model with a row of its own is priced from it; a dated id ("claude-haiku-4-5-20251001") is read from its undated row.
        var row = pricing.Models.FirstOrDefault(m => string.Equals(m.Key, model, StringComparison.OrdinalIgnoreCase));
        if (row.Key is null)
            row = pricing.Models.Where(m => model.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Key.Length).FirstOrDefault();

        return row.Key is null
            ? new AiTaskModelOptionDto(model, false, null, null)
            : new AiTaskModelOptionDto(model, true, row.Value.InputPerMillion, row.Value.OutputPerMillion);
    }

    private static AiTaskModelOptionDto PriceOf(AiPricingOptions pricing, string provider, string model)
    {
        var rates = pricing.RatesForConfiguredModel(provider, model);
        return rates is null
            ? new AiTaskModelOptionDto(model, false, null, null)
            : new AiTaskModelOptionDto(model, true, PerMillion(rates.PromptPer1K), PerMillion(rates.CompletionPer1K));
    }

    private static decimal PerMillion(decimal per1K) => Math.Round(per1K * 1000m, 4);

    private static string ChatModelOf(AiCredentials c, string provider) => provider switch
    {
        "Anthropic" => c.AnthropicModel,
        "OpenAI" => c.OpenAiChatModel,
        _ => c.GoogleChatModel,
    };

    private static string? KeyOf(AiCredentials c, string provider) => provider switch
    {
        "Anthropic" => c.AnthropicApiKey,
        "OpenAI" => c.OpenAiApiKey,
        _ => c.GoogleApiKey,
    };
}
