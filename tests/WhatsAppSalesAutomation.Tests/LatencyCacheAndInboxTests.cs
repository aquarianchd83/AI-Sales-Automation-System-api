using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The plan-features cache shared across requests, and the inbox's newest-activity-first order.</summary>
public sealed class LatencyCacheAndInboxTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FeatureAccessCache _cache = new(new MemoryCache(new MemoryCacheOptions()));
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Plan _trial = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Trial", IsActive = true };
    private readonly Plan _premium = new() { Code = PlatformFeatureCatalog.PremiumPlanCode, Name = "Premium", IsActive = true };

    public LatencyCacheAndInboxTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(_tenant);
        db.Plans.AddRange(_trial, _premium);
        foreach (var feature in PlatformFeatureCatalog.TrialFeatures)
            db.PlanFeatures.Add(new PlanFeature { PlanId = _trial.Id, Feature = feature });
        foreach (var feature in PlatformFeatureCatalog.PremiumFeatures)
            db.PlanFeatures.Add(new PlanFeature { PlanId = _premium.Id, Feature = feature });
        db.Subscriptions.Add(new Subscription { TenantId = _tenant.Id, PlanId = _trial.Id, Status = SubscriptionStatus.Active });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>A fresh context per "request", like the scoped one in the API, wired with the invalidation interceptor.</summary>
    private SqliteApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new FeatureAccessInvalidationInterceptor(_cache))
            .Options;
        return new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
    }

    private async Task<IReadOnlySet<PlatformFeature>> FeaturesInNewRequestAsync(IConfiguration? configuration = null)
    {
        await using var db = NewContext();
        return await new FeatureAccessService(db, configuration, _cache).GetFeaturesAsync(_tenant.Id);
    }

    private const PlatformFeature PremiumOnly = PlatformFeature.LeadDiscovery;

    [Fact]
    public async Task A_later_request_reads_the_plan_from_the_cache_not_the_database()
    {
        Assert.DoesNotContain(PremiumOnly, await FeaturesInNewRequestAsync());

        // Behind EF's back, so nothing invalidates: only a cached answer can still say Trial.
        await using (var db = NewContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE Subscriptions SET PlanId = {0}", _premium.Id);

        Assert.DoesNotContain(PremiumOnly, await FeaturesInNewRequestAsync());
    }

    [Fact]
    public async Task Changing_a_tenants_subscription_drops_its_cached_features()
    {
        Assert.DoesNotContain(PremiumOnly, await FeaturesInNewRequestAsync());

        await using (var db = NewContext())
        {
            var subscription = await db.Subscriptions.IgnoreQueryFilters().SingleAsync();
            subscription.PlanId = _premium.Id;
            await db.SaveChangesAsync();
        }

        Assert.Contains(PremiumOnly, await FeaturesInNewRequestAsync());
    }

    [Fact]
    public async Task Editing_a_plans_features_drops_every_cached_entry()
    {
        Assert.Contains(PlatformFeature.Campaigns, await FeaturesInNewRequestAsync());

        await using (var db = NewContext())
        {
            db.PlanFeatures.Remove(await db.PlanFeatures.SingleAsync(f => f.PlanId == _trial.Id && f.Feature == PlatformFeature.Campaigns));
            await db.SaveChangesAsync();
        }

        Assert.DoesNotContain(PlatformFeature.Campaigns, await FeaturesInNewRequestAsync());
    }

    [Fact]
    public void A_plan_read_before_a_plan_change_is_not_cached()
    {
        var tenantId = Guid.NewGuid();
        var generation = _cache.Generation;
        _cache.InvalidateTenant(Guid.NewGuid()); // a plan change saved while this request was still reading the old plan

        _cache.Set(tenantId, new HashSet<PlatformFeature> { PlatformFeature.Campaigns }, generation);

        Assert.False(_cache.TryGet(tenantId, out _));
    }

    [Fact]
    public async Task An_unpublished_feature_is_withheld_at_once_even_when_the_plan_is_cached()
    {
        Assert.Contains(PlatformFeature.Campaigns, await FeaturesInNewRequestAsync());

        var switchedOff = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PlatformFeatureSwitches.KeyOf(PlatformFeature.Campaigns)] = "false" })
            .Build();

        Assert.DoesNotContain(PlatformFeature.Campaigns, await FeaturesInNewRequestAsync(switchedOff));
        Assert.Contains(PlatformFeature.Campaigns, await FeaturesInNewRequestAsync());
    }

    [Fact]
    public async Task The_inbox_lists_by_last_message_falling_back_to_when_the_conversation_started()
    {
        await using var db = NewContext();
        var phones = 0;
        async Task<Guid> AddAsync(DateTime createdAt, DateTime? lastMessageAt)
        {
            var customer = new Customer { PhoneNumberE164 = $"+9190000000{phones++}" };
            db.Customers.Add(customer);
            var conversation = new Conversation { CustomerId = customer.Id, LastMessageAt = lastMessageAt };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
            conversation.CreatedAt = createdAt;
            await db.SaveChangesAsync();
            return conversation.Id;
        }

        var oldButActive = await AddAsync(new DateTime(2026, 1, 1), new DateTime(2026, 11, 20));
        var newNoMessages = await AddAsync(new DateTime(2026, 11, 10), null);
        var oldQuiet = await AddAsync(new DateTime(2026, 2, 1), new DateTime(2026, 3, 1));

        var service = new ConversationService(db, null!, null!, null!, null!, null!, null!);
        var page = await service.GetPagedAsync(new PagedRequest { Page = 1, PageSize = 10 });

        Assert.Equal(new[] { oldButActive, newNoMessages, oldQuiet }, page.Items.Select(c => c.Id));
        Assert.Null(page.Items[1].LastMessageAt);
    }

    private sealed class AmbientTenant : ITenantContext
    {
        private readonly Guid _tenantId;
        public AmbientTenant(Guid tenantId) => _tenantId = tenantId;
        public Guid? TenantId => _tenantId;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
