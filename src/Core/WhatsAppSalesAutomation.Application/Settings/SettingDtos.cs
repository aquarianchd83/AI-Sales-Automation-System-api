namespace WhatsAppSalesAutomation.Application.Settings;

/// <summary>
/// One setting as returned by the admin API. Secret values are never sent in full - <see
/// cref="Value"/> is null and <see cref="HasValue"/>/<see cref="ValueHint"/> tell the UI whether a
/// secret is already set without exposing it, same masking convention as password-manager UIs.
/// </summary>
public record SettingItemDto(
    string Key,
    string Category,
    bool IsSecret,
    bool IsList,
    string? Description,
    string? Value,
    bool HasValue,
    string? ValueHint);

public record SettingCategoryDto(string Category, IReadOnlyList<SettingItemDto> Items);

/// <summary>
/// Body of PUT /api/v1/settings/{category}. <see cref="Values"/> only needs to carry the keys the
/// caller actually wants to change - a key not present leaves the stored value untouched, which is
/// what lets the UI save a non-secret field in a category without having to re-submit a secret it
/// was never shown. Pass an empty string to explicitly clear a value.
/// </summary>
public record UpdateSettingsRequest(Dictionary<string, string?> Values);
