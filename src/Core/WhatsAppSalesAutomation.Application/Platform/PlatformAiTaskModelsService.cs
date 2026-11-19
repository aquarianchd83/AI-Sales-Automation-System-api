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
/// <param name="ChatModel">The provider's chat model, which the group runs on when nothing is chosen.</param>
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

public record AiTaskModelGroupDto(string Key, string Title, string Description, IReadOnlyList<string> Tasks, IReadOnlyList<AiTaskModelCellDto> Providers);

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

    public PlatformAiTaskModelsService(IAppSettingsStore store, IPlatformAiConfigProvider credentials, IOptionsSnapshot<AiPricingOptions> pricing)
    {
        _store = store;
        _credentials = credentials;
        _pricing = pricing;
    }

    public async Task<AiTaskModelsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return Build(stored);
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

    private AiTaskModelsDto Build(IReadOnlyDictionary<string, string?> stored)
    {
        var credentials = _credentials.Get();
        var pricing = _pricing.Value;

        return new AiTaskModelsDto(AiModelGroups.All.Select(group => new AiTaskModelGroupDto(
            group.Key, group.Title, group.Description, group.Tasks,
            Providers.Select(p => Cell(group, p.Provider, p.Label, credentials, stored, pricing)).ToList())).ToList());
    }

    private static AiTaskModelCellDto Cell(
        AiModelGroup group, string provider, string label, AiCredentials credentials, IReadOnlyDictionary<string, string?> stored, AiPricingOptions pricing)
    {
        var chat = ChatModelOf(credentials, provider);
        stored.TryGetValue(AiModelGroups.SettingKey(group.Key, provider), out var raw);
        var selected = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        var effective = selected ?? chat;

        var priced = pricing.Models
            .Where(m => m.Key.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase))
            .Select(m => new AiTaskModelOptionDto(m.Key[(provider.Length + 1)..], true, PerMillion(m.Value.PromptPer1K), PerMillion(m.Value.CompletionPer1K)))
            .ToList();

        // The models the operator can choose from are the priced ones, plus whatever is chosen or configured now, so a current choice is never missing.
        foreach (var model in new[] { selected, chat }.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!))
        {
            if (!priced.Any(o => string.Equals(o.Model, model, StringComparison.OrdinalIgnoreCase)))
                priced.Add(PriceOf(pricing, provider, model));
        }

        var current = PriceOf(pricing, provider, effective);
        return new AiTaskModelCellDto(
            provider, label, credentials.IsEnabled(provider) && !string.IsNullOrWhiteSpace(KeyOf(credentials, provider)),
            chat, selected, effective, current.HasPrice, current.InputPer1M, current.OutputPer1M,
            priced.OrderBy(o => o.Model, StringComparer.OrdinalIgnoreCase).ToList());
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
