using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Packages;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"AI suggest" on the Lead Discovery profile: the words people type into Google to find the kind of business the
/// tenant is after (for an eye clinic: eye hospital, ophthalmologist, optometrist...), from the target type and the
/// description - from the tenant's own AI when it has one, from what is already known when it does not, and always saying
/// which.</summary>
public sealed class LeadDiscoveryKeywordSuggestionTests : IDisposable
{
    private sealed class FakeAi : IAiTextGenerator
    {
        public string? Reply { get; set; }
        public string? LastPrompt { get; private set; }

        public Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(Reply);
        }
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly FakeAi _ai = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();

    public LeadDiscoveryKeywordSuggestionTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant
        {
            Id = _tenant, Name = "Mine", Slug = "mine", Status = TenantStatus.Active,
            Industry = "Healthcare", IndustrySubcategory = "Eye clinic", CountryCode = "IN",
        });
        _db.Tenants.Add(new Tenant { Id = _otherTenant, Name = "Theirs", Slug = "theirs", Status = TenantStatus.Active, CountryCode = "US" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private LeadDiscoveryKeywordSuggestionService Service() => new(_ai, new SuggestLeadKeywordsRequestValidator());

    [Fact]
    public async Task Uses_the_tenants_AI_when_it_answers_and_says_so()
    {
        _ai.Reply = """["eye hospital", "eye doctor", "ophthalmologist", "optometrist", "netra chikitsalaya"]""";

        var result = await Service().SuggestAsync(new SuggestLeadKeywordsRequest("Selling clinic software", "Eye clinic", null));

        Assert.Equal("AI", result.Source);
        Assert.Equal(new[] { "eye hospital", "eye doctor", "ophthalmologist", "optometrist", "netra chikitsalaya" }, result.Keywords);
    }

    [Fact]
    public async Task Asks_for_what_people_type_into_Google_to_find_the_business_not_for_what_is_being_sold()
    {
        _db.StampTenantId = _tenant;
        _db.SalesPackages.Add(new SalesPackage { Name = "Clinic Gold", Price = 100m, IsActive = true });
        await _db.SaveChangesAsync();
        _ai.Reply = "[]";

        await Service().SuggestAsync(new SuggestLeadKeywordsRequest("We sell record keeping software to clinics", "Eye clinic", new[] { "optician" }));

        Assert.Contains("Kind of business to find: Eye clinic", _ai.LastPrompt);
        Assert.Contains("We sell record keeping software to clinics", _ai.LastPrompt);
        Assert.Contains("type into Google", _ai.LastPrompt);
        Assert.Contains("ophthalmologist", _ai.LastPrompt); // the worked example
        Assert.Contains("local-language", _ai.LastPrompt);
        Assert.Contains("do not repeat): optician", _ai.LastPrompt);
        Assert.DoesNotContain("Clinic Gold", _ai.LastPrompt); // packages describe what is sold, not who to find
    }

    [Fact]
    public async Task Uses_only_what_is_written_on_the_lead_discovery_profile_and_nothing_from_the_company_profile()
    {
        _ai.Reply = "[]";

        await Service().SuggestAsync(new SuggestLeadKeywordsRequest("Selling record keeping software to clinics", "Eye clinic", null));

        Assert.DoesNotContain("Healthcare", _ai.LastPrompt);   // the company's industry
        Assert.DoesNotContain("Country", _ai.LastPrompt);
        Assert.DoesNotContain("Mine", _ai.LastPrompt);          // the company's name
    }

    [Fact]
    public async Task Never_suggests_what_is_already_added()
    {
        _ai.Reply = """["optician", "Eye Clinic", "eyewear store"]""";

        var result = await Service().SuggestAsync(new SuggestLeadKeywordsRequest("x", null, new[] { "eye clinic", "optician" }));

        Assert.Equal(new[] { "eyewear store" }, result.Keywords);
    }

    [Fact]
    public async Task Falls_back_to_the_target_type_and_says_so_when_the_AI_gives_nothing()
    {
        _ai.Reply = null;

        var result = await Service().SuggestAsync(new SuggestLeadKeywordsRequest("Practice software", "Eye clinic", null));

        Assert.Equal("Common terms", result.Source);
        Assert.Contains("Eye clinic", result.Keywords);
    }

    [Fact]
    public async Task Asks_for_a_description_first()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service().SuggestAsync(new SuggestLeadKeywordsRequest("  ", null, null)));
    }

    [Fact]
    public async Task Offers_at_most_fifteen()
    {
        _ai.Reply = "[" + string.Join(",", Enumerable.Range(1, 30).Select(i => $"\"term {i}\"")) + "]";

        var result = await Service().SuggestAsync(new SuggestLeadKeywordsRequest("x", null, null));

        Assert.Equal(LeadDiscoveryKeywordSuggestionService.MaxSuggestions, result.Keywords.Count);
        Assert.Equal(15, result.Keywords.Count);
    }
}
