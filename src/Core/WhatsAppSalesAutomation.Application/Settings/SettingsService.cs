using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Application.Settings;

public class SettingsService : ISettingsService
{
    private readonly IAppSettingsStore _store;
    private readonly IProviderModelCatalog? _models;

    /// <param name="models">Where a setting that names a model (<see cref="AppSettingDefinition.ModelFrom"/>) gets its choices: the AI Provider Models
    /// catalogue. Without it such a setting is a plain text value.</param>
    public SettingsService(IAppSettingsStore store, IProviderModelCatalog? models = null)
    {
        _store = store;
        _models = models;
    }

    public async Task<IReadOnlyList<SettingCategoryDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);

        var categories = new List<SettingCategoryDto>();
        foreach (var category in AppSettingCatalog.Categories)
            categories.Add(await BuildCategoryAsync(category, stored, cancellationToken));
        return categories;
    }

    public async Task<SettingCategoryDto> GetCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        EnsureKnownCategory(category);
        var stored = await _store.GetAllAsync(cancellationToken);
        return await BuildCategoryAsync(category, stored, cancellationToken);
    }

    public async Task UpdateCategoryAsync(string category, UpdateSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        EnsureKnownCategory(category);

        var categoryKeys = new HashSet<string>(
            AppSettingCatalog.All.Where(d => d.Category == category).Select(d => d.Key),
            StringComparer.OrdinalIgnoreCase);

        var unknownKeys = request.Values.Keys.Where(k => !categoryKeys.Contains(k)).ToList();
        if (unknownKeys.Count > 0)
            throw new NotFoundException($"'{string.Join("', '", unknownKeys)}' is not a setting under the '{category}' category.");

        IReadOnlyDictionary<string, string?>? stored = null;

        // A setting with fixed choices only ever stores one of them, whatever the caller sent.
        foreach (var (key, value) in request.Values)
        {
            var definition = AppSettingCatalog.All.First(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            var options = definition.Options;
            if (options is not null && !string.IsNullOrEmpty(value) && !options.Contains(value, StringComparer.OrdinalIgnoreCase))
                throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure(key, $"{key} must be one of: {string.Join(", ", options)}.") });

            // A model setting takes a model from the AI Provider Models catalogue, or the value it already holds (so saving the page never fails over a
            // model that was running before the catalogue had it). Anything else is added to the catalogue first.
            if (definition.ModelFrom is not null && _models is not null && !string.IsNullOrWhiteSpace(value))
            {
                stored ??= await _store.GetAllAsync(cancellationToken);
                var choices = await ModelChoicesAsync(definition, stored.GetValueOrDefault(definition.Key), cancellationToken);
                if (!choices.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                    throw new FluentValidation.ValidationException(new[]
                    {
                        new FluentValidation.Results.ValidationFailure(key, $"'{value.Trim()}' is not a {definition.ModelFrom.Provider} {definition.ModelFrom.Type.ToString().ToLowerInvariant()} model in the AI Provider Models catalogue. Add it there first.")
                    });
            }

            // A whole-number setting stays inside its range. Empty is allowed: it clears the stored value, so the built-in default applies.
            if ((definition.MinValue is not null || definition.MaxValue is not null) && !string.IsNullOrWhiteSpace(value))
            {
                var min = definition.MinValue ?? 0;
                var max = definition.MaxValue ?? int.MaxValue;
                if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < min || number > max)
                    throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure(key, $"{key} must be a whole number from {min} to {max}.") });
            }
        }

        await _store.UpsertAsync(request.Values, updatedByUserId, cancellationToken);
    }

    /// <summary>The catalogue's models of the setting's provider and type, plus the value the setting holds now, so a running choice is never missing.</summary>
    private async Task<IReadOnlyList<string>> ModelChoicesAsync(AppSettingDefinition definition, string? current, CancellationToken cancellationToken)
    {
        var source = definition.ModelFrom!;
        var choices = (await _models!.ListAsync(source.Provider, source.Type, cancellationToken)).ToList();
        if (!string.IsNullOrWhiteSpace(current) && !choices.Contains(current.Trim(), StringComparer.OrdinalIgnoreCase))
            choices.Add(current.Trim());
        return choices;
    }

    private async Task<SettingCategoryDto> BuildCategoryAsync(string category, IReadOnlyDictionary<string, string?> stored, CancellationToken cancellationToken)
    {
        var items = new List<SettingItemDto>();
        foreach (var definition in AppSettingCatalog.All.Where(d => d.Category == category && !d.HiddenInEditor))
        {
            var storedValue = stored.GetValueOrDefault(definition.Key);
            var options = definition.Options;
            if (definition.ModelFrom is not null && _models is not null && !definition.IsSecret)
                options = await ModelChoicesAsync(definition, storedValue, cancellationToken);

            items.Add(ToDto(definition, storedValue, options));
        }

        return new SettingCategoryDto(category, items);
    }

    private static SettingItemDto ToDto(AppSettingDefinition definition, string? storedValue, IReadOnlyList<string>? options)
    {
        var hasValue = !string.IsNullOrEmpty(storedValue);

        if (!definition.IsSecret)
            return new SettingItemDto(definition.Key, definition.Category, definition.IsSecret, definition.IsList, definition.Description, storedValue, hasValue, null, options);

        // Never round-trip a real secret value - only enough of a hint to confirm to an admin that
        // the value they expect is the one currently stored, same convention as a password manager.
        var hint = hasValue ? MaskSecret(storedValue!) : null;
        return new SettingItemDto(definition.Key, definition.Category, definition.IsSecret, definition.IsList, definition.Description, null, hasValue, hint);
    }

    private static string MaskSecret(string value)
        => value.Length <= 4 ? "••••" : $"••••{value[^4..]}";

    private static void EnsureKnownCategory(string category)
    {
        if (!AppSettingCatalog.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new NotFoundException($"Settings category '{category}' was not found.");
    }
}
