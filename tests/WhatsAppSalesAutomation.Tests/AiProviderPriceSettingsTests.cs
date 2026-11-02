using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Settings;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The per-provider token prices are now only a fallback behind "Model token prices", so the editor no longer offers them - they are still
/// settings (stored, seeded and read), just not listed.</summary>
public sealed class AiProviderPriceSettingsTests
{
    private sealed class EmptyStore : IAppSettingsStore
    {
        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static readonly string[] PriceKeys =
    {
        "AiProviders:OpenAI:InputPer1MUsd", "AiProviders:OpenAI:OutputPer1MUsd",
        "AiProviders:Anthropic:InputPer1MUsd", "AiProviders:Anthropic:OutputPer1MUsd",
        "AiProviders:Google:InputPer1MUsd", "AiProviders:Google:OutputPer1MUsd",
    };

    [Fact]
    public async Task The_editor_does_not_offer_the_old_per_provider_prices()
    {
        var category = await new SettingsService(new EmptyStore()).GetCategoryAsync("AiProviders");

        Assert.DoesNotContain(category.Items, i => PriceKeys.Contains(i.Key));
        Assert.Contains(category.Items, i => i.Key == "AiProviders:OpenAI:ChatModel");
    }

    [Fact]
    public void The_old_prices_remain_real_settings_so_a_model_without_a_row_still_has_a_fallback()
    {
        foreach (var key in PriceKeys)
        {
            var definition = AppSettingCatalog.All.Single(d => d.Key == key);
            Assert.True(definition.HiddenInEditor, key);
            Assert.Equal("0", definition.DefaultValue);
        }

        Assert.All(AppSettingCatalog.All.Where(d => d.HiddenInEditor), d => Assert.Contains(d.Key, PriceKeys));
    }
}
