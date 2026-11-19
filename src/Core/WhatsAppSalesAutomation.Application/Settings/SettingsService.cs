using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Settings;

public class SettingsService : ISettingsService
{
    private readonly IAppSettingsStore _store;

    public SettingsService(IAppSettingsStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<SettingCategoryDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);

        return AppSettingCatalog.Categories
            .Select(category => BuildCategory(category, stored))
            .ToList();
    }

    public async Task<SettingCategoryDto> GetCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        EnsureKnownCategory(category);
        var stored = await _store.GetAllAsync(cancellationToken);
        return BuildCategory(category, stored);
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

        // A setting with fixed choices only ever stores one of them, whatever the caller sent.
        foreach (var (key, value) in request.Values)
        {
            var definition = AppSettingCatalog.All.First(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            var options = definition.Options;
            if (options is not null && !string.IsNullOrEmpty(value) && !options.Contains(value, StringComparer.OrdinalIgnoreCase))
                throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure(key, $"{key} must be one of: {string.Join(", ", options)}.") });

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

    private static SettingCategoryDto BuildCategory(string category, IReadOnlyDictionary<string, string?> stored)
    {
        var items = AppSettingCatalog.All
            .Where(d => d.Category == category && !d.HiddenInEditor)
            .Select(d => ToDto(d, stored.GetValueOrDefault(d.Key)))
            .ToList();

        return new SettingCategoryDto(category, items);
    }

    private static SettingItemDto ToDto(AppSettingDefinition definition, string? storedValue)
    {
        var hasValue = !string.IsNullOrEmpty(storedValue);

        if (!definition.IsSecret)
            return new SettingItemDto(definition.Key, definition.Category, definition.IsSecret, definition.IsList, definition.Description, storedValue, hasValue, null, definition.Options);

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
