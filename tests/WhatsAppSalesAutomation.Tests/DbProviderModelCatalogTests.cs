using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"Model by task" offers the models in the platform's own catalogue (AI Provider Models), not the ones a provider's API lists.</summary>
public sealed class DbProviderModelCatalogTests : IDisposable
{
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
    private readonly DbProviderModelCatalog _catalog;

    public DbProviderModelCatalogTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _catalog = new DbProviderModelCatalog(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Add(string provider, string modelId, AiModelType type = AiModelType.Chat, bool active = true) =>
        _db.AiProviderModels.Add(new AiProviderModel { Provider = provider, DisplayName = modelId, ModelId = modelId, ModelType = type, IsActive = active });

    [Fact]
    public async Task Lists_the_active_chat_models_of_the_provider()
    {
        Add("OpenAI", "gpt-5-nano");
        Add("OpenAI", "gpt-5");
        Add("Anthropic", "claude-haiku-4-5");
        await _db.SaveChangesAsync();

        Assert.Equal(new[] { "gpt-5", "gpt-5-nano" }, (await _catalog.ListAsync("OpenAI")).OrderBy(m => m));
        Assert.Equal(new[] { "claude-haiku-4-5" }, await _catalog.ListAsync("Anthropic"));
    }

    [Fact]
    public async Task Leaves_out_embedding_and_image_models_and_ones_switched_off()
    {
        Add("OpenAI", "gpt-5");
        Add("OpenAI", "text-embedding-3-small", AiModelType.Embedding);
        Add("OpenAI", "gpt-image-1", AiModelType.Image);
        Add("OpenAI", "gpt-5-retired", active: false);
        await _db.SaveChangesAsync();

        Assert.Equal(new[] { "gpt-5" }, await _catalog.ListAsync("OpenAI"));
    }

    [Fact]
    public async Task Matches_the_provider_whatever_its_case_and_lists_nothing_for_one_with_no_models()
    {
        Add("OpenAI", "gpt-5");
        await _db.SaveChangesAsync();

        Assert.Equal(new[] { "gpt-5" }, await _catalog.ListAsync("openai"));
        Assert.Empty(await _catalog.ListAsync("Google"));
    }
}
