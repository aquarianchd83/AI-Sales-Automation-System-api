using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Settings;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Content Studio platform settings: managed only in the Platform Admin Console, so each carries its own default, and the ones with
/// fixed choices are dropdowns that refuse anything else.</summary>
public sealed class ContentStudioSettingsCatalogTests
{
    private sealed class FakeStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Saved { get; } = new();

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Saved);

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values) Saved[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static readonly IReadOnlyList<AppSettingDefinition> Studio =
        AppSettingCatalog.All.Where(d => d.Category == "ContentStudio").ToList();

    [Fact]
    public void Every_studio_setting_has_a_default_so_the_console_never_opens_blank()
    {
        Assert.Equal(4, Studio.Count);
        Assert.All(Studio, d => Assert.False(string.IsNullOrWhiteSpace(d.DefaultValue), d.Key));
    }

    [Fact]
    public void A_default_is_always_one_of_the_settings_own_options()
    {
        foreach (var d in Studio.Where(d => d.Options is not null))
            Assert.Contains(d.DefaultValue, d.Options!);
    }

    [Fact]
    public void The_module_is_off_by_default_and_storage_defaults_to_the_public_bucket()
    {
        Assert.Equal("false", Studio.Single(d => d.Key == "ContentStudio:Enabled").DefaultValue);
        Assert.Equal("PublicBucket", Studio.Single(d => d.Key == "ContentStudio:StorageMode").DefaultValue);
    }

    [Fact]
    public void The_settings_with_fixed_choices_are_the_ones_that_become_dropdowns()
    {
        Assert.Equal(new[] { "true", "false" }, Studio.Single(d => d.Key == "ContentStudio:Enabled").Options);
        Assert.Equal(new[] { "PublicBucket", "SignedUrls" }, Studio.Single(d => d.Key == "ContentStudio:StorageMode").Options);
        Assert.NotNull(Studio.Single(d => d.Key == "ContentStudio:SignedUrlMinutes").Options);
        Assert.Null(Studio.Single(d => d.Key == "ContentStudio:StorageKeyPrefix").Options);
    }

    [Fact]
    public async Task The_console_is_told_which_settings_are_dropdowns()
    {
        var category = await new SettingsService(new FakeStore()).GetCategoryAsync("ContentStudio");

        Assert.Equal(new[] { "true", "false" }, category.Items.Single(i => i.Key == "ContentStudio:Enabled").Options);
        Assert.Null(category.Items.Single(i => i.Key == "ContentStudio:StorageKeyPrefix").Options);
    }

    [Fact]
    public async Task A_dropdown_setting_refuses_a_value_that_is_not_one_of_its_options()
    {
        var store = new FakeStore();
        var service = new SettingsService(store);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            service.UpdateCategoryAsync("ContentStudio", new UpdateSettingsRequest(new() { ["ContentStudio:StorageMode"] = "Dropbox" }), null));
        Assert.Empty(store.Saved);

        await service.UpdateCategoryAsync("ContentStudio", new UpdateSettingsRequest(new() { ["ContentStudio:StorageMode"] = "SignedUrls" }), null);
        Assert.Equal("SignedUrls", store.Saved["ContentStudio:StorageMode"]);
    }

    [Fact]
    public async Task A_free_text_setting_still_takes_any_value()
    {
        var store = new FakeStore();

        await new SettingsService(store).UpdateCategoryAsync("ContentStudio", new UpdateSettingsRequest(new() { ["ContentStudio:StorageKeyPrefix"] = "videos" }), null);

        Assert.Equal("videos", store.Saved["ContentStudio:StorageKeyPrefix"]);
    }

    [Fact]
    public void Every_option_list_in_the_whole_catalog_has_unique_values_and_a_valid_default()
    {
        foreach (var d in AppSettingCatalog.All.Where(d => d.Options is not null))
        {
            Assert.Equal(d.Options!.Count, d.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            if (d.DefaultValue is not null)
                Assert.Contains(d.DefaultValue, d.Options);
        }
    }
}
