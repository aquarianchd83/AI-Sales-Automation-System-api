using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A setting that names an AI model is a dropdown of the AI Provider Models catalogue, not a text box.</summary>
public sealed class ModelSettingsFromCatalogTests : IDisposable
{
    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new();
        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Rows);
        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values) Rows[key] = value;
            return Task.CompletedTask;
        }
        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly MemoryStore _store = new();
    private readonly SettingsService _service;

    public ModelSettingsFromCatalogTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _service = new SettingsService(_store, new DbProviderModelCatalog(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Add(string provider, string modelId, AiModelType type = AiModelType.Chat, bool active = true) =>
        _db.AiProviderModels.Add(new AiProviderModel { Provider = provider, DisplayName = modelId, ModelId = modelId, ModelType = type, IsActive = active });

    private async Task<SettingItemDto> Item(string category, string key) =>
        (await _service.GetCategoryAsync(category)).Items.Single(i => i.Key == key);

    private Task Save(string category, string key, string value) =>
        _service.UpdateCategoryAsync(category, new UpdateSettingsRequest(new Dictionary<string, string?> { [key] = value }), null);

    [Fact]
    public void Every_setting_that_names_a_model_is_tied_to_a_provider_and_type()
    {
        var models = AppSettingCatalog.All.Where(d => d.ModelFrom is not null).ToDictionary(d => d.Key, d => d.ModelFrom!);

        Assert.Equal(new AiModelType[] { AiModelType.Chat }, new[] { models["AiProviders:Anthropic:Model"].Type });
        Assert.Equal(AiModelType.Embedding, models["AiProviders:OpenAI:EmbeddingModel"].Type);
        Assert.Equal(AiModelType.Embedding, models["AiProviders:Google:EmbeddingModel"].Type);
        Assert.Equal(("Google", AiModelType.Video), (models["ContentStudio:Cinematic:VeoModel"].Provider, models["ContentStudio:Cinematic:VeoModel"].Type));
        Assert.Equal(("OpenAI", AiModelType.Image), (models["ContentStudio:Cinematic:ImageModel"].Provider, models["ContentStudio:Cinematic:ImageModel"].Type));
        Assert.Equal(("OpenAI", AiModelType.Speech), (models["ContentStudio:Cinematic:SpeechModel"].Provider, models["ContentStudio:Cinematic:SpeechModel"].Type));
        Assert.Equal(8, models.Count);
    }

    [Fact]
    public async Task The_choices_are_the_active_models_of_that_provider_and_type_in_the_catalogue()
    {
        Add("OpenAI", "gpt-5");
        Add("OpenAI", "gpt-5-nano");
        Add("OpenAI", "gpt-old", active: false);
        Add("OpenAI", "text-embedding-3-small", AiModelType.Embedding);
        Add("Google", "gemini-2.5-flash");
        await _db.SaveChangesAsync();

        var chat = await Item("AiProviders", "AiProviders:OpenAI:ChatModel");
        var embedding = await Item("AiProviders", "AiProviders:OpenAI:EmbeddingModel");

        Assert.Equal(new[] { "gpt-5", "gpt-5-nano" }, chat.Options!.OrderBy(o => o));
        Assert.Equal(new[] { "text-embedding-3-small" }, embedding.Options);
    }

    [Fact]
    public async Task The_model_the_setting_holds_now_is_always_one_of_the_choices()
    {
        Add("OpenAI", "gpt-5");
        await _db.SaveChangesAsync();
        _store.Rows["AiProviders:OpenAI:ChatModel"] = "gpt-4.1-legacy";

        var chat = await Item("AiProviders", "AiProviders:OpenAI:ChatModel");

        Assert.Contains("gpt-4.1-legacy", chat.Options!);
        Assert.Contains("gpt-5", chat.Options!);
    }

    [Fact]
    public async Task A_model_from_the_catalogue_can_be_saved_and_so_can_the_one_already_held()
    {
        Add("OpenAI", "gpt-5");
        await _db.SaveChangesAsync();
        _store.Rows["AiProviders:OpenAI:ChatModel"] = "gpt-4.1-legacy";

        await Save("AiProviders", "AiProviders:OpenAI:ChatModel", "gpt-5");
        Assert.Equal("gpt-5", _store.Rows["AiProviders:OpenAI:ChatModel"]);

        _store.Rows["AiProviders:OpenAI:ChatModel"] = "gpt-4.1-legacy";
        await Save("AiProviders", "AiProviders:OpenAI:ChatModel", "gpt-4.1-legacy");
        Assert.Equal("gpt-4.1-legacy", _store.Rows["AiProviders:OpenAI:ChatModel"]);
    }

    [Fact]
    public async Task A_model_that_is_not_in_the_catalogue_is_refused_with_a_pointer_to_where_to_add_it()
    {
        Add("OpenAI", "gpt-5");
        await _db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() => Save("AiProviders", "AiProviders:OpenAI:ChatModel", "gpt-typed-by-hand"));

        Assert.Contains("AI Provider Models catalogue", error.Errors.Single().ErrorMessage);
        Assert.DoesNotContain("AiProviders:OpenAI:ChatModel", _store.Rows.Keys);
    }

    [Fact]
    public async Task A_model_of_the_wrong_type_is_refused()
    {
        Add("OpenAI", "text-embedding-3-small", AiModelType.Embedding);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() => Save("AiProviders", "AiProviders:OpenAI:ChatModel", "text-embedding-3-small"));
    }

    [Fact]
    public async Task Clearing_the_setting_is_still_allowed()
    {
        Add("OpenAI", "gpt-5");
        await _db.SaveChangesAsync();

        await Save("AiProviders", "AiProviders:OpenAI:ChatModel", "");

        Assert.Equal("", _store.Rows["AiProviders:OpenAI:ChatModel"]);
    }

    [Fact]
    public async Task A_setting_that_is_not_a_model_is_unchanged()
    {
        var baseUrl = await Item("AiProviders", "AiProviders:OpenAI:BaseUrl");

        Assert.Null(baseUrl.Options);
    }
}
