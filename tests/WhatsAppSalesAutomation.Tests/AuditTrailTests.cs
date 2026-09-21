using System.Text.Json;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The audit trail's two halves: what the interceptor captures, and what the service lets an Admin read.
///
/// The interceptor tests are mostly about what is NOT recorded. That an audit log records a stage
/// change is unremarkable; that it does not also quietly record every customer's phone number is the
/// property that a "capture everything" design would have failed, invisibly, from the first day.
/// </summary>
public sealed class AuditTrailTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeUser _user = new();
    private readonly TestClock _clock = new() { UtcNow = Now };
    private readonly SqliteApplicationDbContext _db;

    public AuditTrailTests()
    {
        _connection.Open();
        _db = NewContext(TenantA, withAudit: true);
        _db.Database.EnsureCreated();
    }

    private SqliteApplicationDbContext NewContext(Guid? tenant, bool withAudit)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection);
        if (withAudit)
            builder.AddInterceptors(new AuditTrailSaveChangesInterceptor(_user, _clock));

        return new SqliteApplicationDbContext(builder.Options, new StubTenant(tenant), new AnonymousUser()) { StampTenantId = tenant };
    }

    private List<AuditLog> Logs(Guid? entityId = null) => _db.AuditLogs.AsNoTracking().IgnoreQueryFilters()
        .Where(a => entityId == null || a.EntityId == entityId).OrderBy(a => a.PerformedAt).ToList();

    private Customer NewCustomer(Guid? tenant = null) => new()
    {
        TenantId = tenant ?? TenantA, PhoneNumberE164 = "+919812345678", FirstName = "Rahul", Email = "rahul@private.example",
        OptInStatus = OptInStatus.PendingOptIn
    };

    private (Customer Customer, Lead Lead) NewLead()
    {
        var customer = NewCustomer();
        _db.Customers.Add(customer);
        var lead = new Lead { TenantId = TenantA, CustomerId = customer.Id, Stage = LeadStage.New, Score = LeadScoreBand.Cold };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return (customer, lead);
    }

    // ── What is captured ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_stage_change_is_recorded_with_from_and_to_and_is_a_status_change()
    {
        var (_, lead) = NewLead();
        _user.UserId = Guid.NewGuid();

        lead.Stage = LeadStage.Qualified;
        _db.SaveChanges();

        var entry = Logs(lead.Id).Single(a => a.Action == AuditAction.StatusChange);
        Assert.Equal("Lead", entry.EntityName);
        Assert.Equal(_user.UserId, entry.PerformedBy);

        var changes = JsonDocument.Parse(entry.ChangesJson).RootElement;
        Assert.Equal("New", changes.GetProperty("Stage").GetProperty("from").GetString());
        Assert.Equal("Qualified", changes.GetProperty("Stage").GetProperty("to").GetString());
    }

    [Fact]
    public void The_change_and_its_record_are_saved_together()
    {
        var (_, lead) = NewLead();
        lead.Stage = LeadStage.Won;
        lead.AssignedTo = Guid.NewGuid();

        var written = _db.SaveChanges();

        // One SaveChanges wrote the lead update AND the audit row: 2 rows, one transaction, so there
        // is never a change without its record or a record of a change that did not happen.
        Assert.Equal(2, written);
    }

    [Fact]
    public void Creation_records_the_allow_listed_values_only()
    {
        var customer = NewCustomer();
        _db.Customers.Add(customer);
        _db.SaveChanges();

        var entry = Logs(customer.Id).Single();

        Assert.Equal(AuditAction.Create, entry.Action);
        Assert.Contains("PendingOptIn", entry.ChangesJson);
    }

    [Fact]
    public void Personal_data_never_reaches_the_audit_log()
    {
        // The reason the catalogue is an allow-list. Opt-in state is audited (a compliance record);
        // the phone number, name and email sitting on the same row must not be.
        var customer = NewCustomer();
        _db.Customers.Add(customer);
        _db.SaveChanges();
        customer.OptInStatus = OptInStatus.OptedOut;
        customer.FirstName = "Someone Else";
        _db.SaveChanges();

        var everything = string.Join(" ", Logs(customer.Id).Select(a => a.ChangesJson));

        Assert.DoesNotContain("9812345678", everything);
        Assert.DoesNotContain("Rahul", everything);
        Assert.DoesNotContain("Someone Else", everything);
        Assert.DoesNotContain("private.example", everything);
    }

    [Fact]
    public void Only_the_properties_that_actually_changed_are_recorded()
    {
        var (_, lead) = NewLead();
        lead.Stage = LeadStage.Qualifying;   // changed
        lead.AssignedTo = null;              // unchanged (already null)
        _db.SaveChanges();

        var changes = JsonDocument.Parse(Logs(lead.Id).Last().ChangesJson).RootElement;

        Assert.True(changes.TryGetProperty("Stage", out _));
        Assert.False(changes.TryGetProperty("AssignedTo", out _));
    }

    [Fact]
    public void A_change_to_a_non_audited_property_writes_nothing()
    {
        var (_, lead) = NewLead();
        var before = Logs().Count;

        lead.Budget = "50 lakh";   // a real column, deliberately not in the catalogue
        _db.SaveChanges();

        Assert.Equal(before, Logs().Count);
    }

    [Fact]
    public void A_non_lifecycle_change_is_an_update_not_a_status_change()
    {
        var campaign = new Campaign { TenantId = TenantA, Name = "Diwali", Status = CampaignStatus.Draft, CreatedBy = Guid.NewGuid() };
        _db.Campaigns.Add(campaign);
        _db.SaveChanges();

        campaign.Name = "Diwali 2026";
        _db.SaveChanges();

        Assert.Equal(AuditAction.Update, Logs(campaign.Id).Last().Action);
    }

    [Fact]
    public void A_soft_delete_is_recorded_as_a_delete()
    {
        var customer = NewCustomer();
        _db.Customers.Add(customer);
        _db.SaveChanges();

        customer.IsDeleted = true;
        _db.SaveChanges();

        Assert.Equal(AuditAction.Delete, Logs(customer.Id).Last().Action);
    }

    [Fact]
    public void Entities_outside_the_catalogue_are_not_audited()
    {
        // A real tenant-scoped table that is deliberately not in the catalogue.
        _db.KnowledgeBaseArticleVersions.Add(new KnowledgeBaseArticleVersion
        {
            TenantId = TenantA, ArticleId = Guid.NewGuid(), ArticleKey = "k", VersionNumber = 1, Title = "t", Content = "c",
            ContentHash = "h", MetadataJson = "{}", PublishedBy = Guid.NewGuid(), PublishedAt = Now, ApprovedBy = Guid.NewGuid(), ApprovedAt = Now
        });
        _db.SaveChanges();

        Assert.Empty(Logs());
    }

    [Fact]
    public void Several_changes_in_one_save_each_get_their_own_row()
    {
        var (_, lead) = NewLead();
        var customer = _db.Customers.Single(c => c.Id == lead.CustomerId);
        var before = Logs().Count;

        lead.Stage = LeadStage.Negotiation;
        customer.OptInStatus = OptInStatus.OptedIn;
        _db.SaveChanges();

        Assert.Equal(before + 2, Logs().Count);
    }

    // ── Who, when, where ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_actor_ip_time_and_impersonator_are_recorded()
    {
        var (_, lead) = NewLead();
        _user.UserId = Guid.NewGuid();
        _user.Impersonator = Guid.NewGuid();
        _user.Ip = "203.0.113.9";

        lead.Stage = LeadStage.Lost;
        _db.SaveChanges();

        var entry = Logs(lead.Id).Last();
        Assert.Equal(_user.Ip, entry.IpAddress);
        Assert.Equal(Now, entry.PerformedAt);
        // The tenant is entitled to see that the platform touched their data, and who.
        Assert.Equal(_user.Impersonator, entry.ImpersonatedBy);
    }

    [Fact]
    public void A_change_by_the_system_has_no_actor_rather_than_a_made_up_one()
    {
        var (_, lead) = NewLead();
        _user.UserId = null;   // a background job, the AI agent, a webhook

        lead.Stage = LeadStage.Qualified;
        _db.SaveChanges();

        Assert.Null(Logs(lead.Id).Last().PerformedBy);
    }

    [Fact]
    public void The_entry_belongs_to_the_audited_entitys_tenant_not_the_ambient_one()
    {
        // A support session or a job running under another scope changes tenant B's data; the trail
        // has to land in tenant B's log, where B can see it, and not in the caller's.
        using var platform = NewContext(tenant: null, withAudit: true);
        var customer = NewCustomer(TenantB);
        platform.Customers.Add(customer);
        platform.SaveChanges();

        Assert.Equal(TenantB, Logs(customer.Id).Single().TenantId);
    }

    [Fact]
    public void A_global_article_change_has_no_tenant_to_show_a_trail_to_and_writes_nothing()
    {
        using var platform = NewContext(tenant: null, withAudit: true);
        platform.KnowledgeBaseArticles.Add(new KnowledgeBaseArticle
        {
            TenantId = null, ArticleKey = "policy", Title = "Policy", Content = "x", ContentHash = "h",
            SourceType = KnowledgeSourceType.PlatformPolicy, AuthorityRank = 90, LanguageCode = "en", EffectiveFrom = Now
        });
        platform.SaveChanges();

        Assert.Empty(Logs());
    }

    // ── Append-only ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_audit_entry_cannot_be_modified()
    {
        var (_, lead) = NewLead();
        lead.Stage = LeadStage.Won;
        _db.SaveChanges();

        var entry = _db.AuditLogs.IgnoreQueryFilters().First();
        entry.ChangesJson = "{}";

        var ex = Assert.Throws<InvalidOperationException>(() => _db.SaveChanges());
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void An_audit_entry_cannot_be_deleted()
    {
        NewLead();
        var entry = _db.AuditLogs.IgnoreQueryFilters().First();

        _db.AuditLogs.Remove(entry);

        Assert.Throws<InvalidOperationException>(() => _db.SaveChanges());
    }

    // ── The service ──────────────────────────────────────────────────────────────────────

    private AuditLogService NewService(Guid tenant) => new(NewContext(tenant, withAudit: false));

    private AuditLog Seed(Guid tenant, string entity, AuditAction action, DateTime at, Guid? by = null, Guid? entityId = null)
    {
        var log = new AuditLog
        {
            TenantId = tenant, EntityName = entity, EntityId = entityId ?? Guid.NewGuid(), Action = action,
            ChangesJson = "{}", PerformedBy = by, PerformedAt = at, CreatedAt = at
        };
        _db.AuditLogs.Add(log);
        _db.SaveChanges();
        return log;
    }

    [Fact]
    public async Task The_list_is_newest_first()
    {
        Seed(TenantA, "Lead", AuditAction.Update, Now.AddHours(-3));
        Seed(TenantA, "Lead", AuditAction.Update, Now.AddHours(-1));
        Seed(TenantA, "Lead", AuditAction.Update, Now.AddHours(-2));

        var page = await NewService(TenantA).GetPagedAsync(new AuditLogQuery());

        Assert.Equal(new[] { -1, -2, -3 }, page.Items.Select(i => (int)(i.PerformedAt - Now).TotalHours));
    }

    [Fact]
    public async Task A_tenant_never_sees_another_tenants_entries()
    {
        Seed(TenantA, "Lead", AuditAction.Update, Now);
        Seed(TenantB, "Lead", AuditAction.Update, Now);

        var page = await NewService(TenantA).GetPagedAsync(new AuditLogQuery());

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Filters_narrow_by_entity_action_actor_and_time()
    {
        var alice = Guid.NewGuid();
        var lead = Guid.NewGuid();
        Seed(TenantA, "Lead", AuditAction.StatusChange, Now.AddDays(-1), alice, lead);
        Seed(TenantA, "Lead", AuditAction.Update, Now.AddDays(-1), Guid.NewGuid());
        Seed(TenantA, "Campaign", AuditAction.StatusChange, Now.AddDays(-10), alice);

        var service = NewService(TenantA);

        Assert.Equal(2, (await service.GetPagedAsync(new AuditLogQuery { EntityName = "Lead" })).TotalCount);
        Assert.Equal(2, (await service.GetPagedAsync(new AuditLogQuery { Action = "statuschange" })).TotalCount);   // case-insensitive
        Assert.Equal(2, (await service.GetPagedAsync(new AuditLogQuery { PerformedBy = alice })).TotalCount);
        Assert.Equal(1, (await service.GetPagedAsync(new AuditLogQuery { EntityId = lead })).TotalCount);
        Assert.Equal(2, (await service.GetPagedAsync(new AuditLogQuery { From = Now.AddDays(-2) })).TotalCount);
        Assert.Equal(1, (await service.GetPagedAsync(new AuditLogQuery { To = Now.AddDays(-5) })).TotalCount);
    }

    [Fact]
    public async Task Paging_returns_the_requested_slice_and_the_full_count()
    {
        for (var i = 0; i < 25; i++)
            Seed(TenantA, "Lead", AuditAction.Update, Now.AddMinutes(-i));

        var page = await NewService(TenantA).GetPagedAsync(new AuditLogQuery { Page = 2, PageSize = 10 });

        Assert.Equal(10, page.Items.Count);
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
    }

    [Fact]
    public async Task The_actors_name_is_resolved_and_a_system_change_has_none()
    {
        var userId = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser { Id = userId, TenantId = TenantA, FullName = "Priya Sharma", UserName = "priya", Email = "p@x.com" });
        _db.SaveChanges();
        Seed(TenantA, "Lead", AuditAction.Update, Now.AddMinutes(-1), userId);
        Seed(TenantA, "Lead", AuditAction.Update, Now.AddMinutes(-2), by: null);

        var items = (await NewService(TenantA).GetPagedAsync(new AuditLogQuery())).Items;

        Assert.Equal("Priya Sharma", items[0].PerformedByName);
        Assert.Null(items[1].PerformedBy);
        Assert.Null(items[1].PerformedByName);
    }

    [Fact]
    public async Task An_unknown_action_is_rejected_with_the_valid_ones_listed()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            NewService(TenantA).GetPagedAsync(new AuditLogQuery { Action = "Explode" }));

        Assert.Contains("StatusChange", ex.Message);
    }

    [Fact]
    public async Task A_reversed_date_range_is_rejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            NewService(TenantA).GetPagedAsync(new AuditLogQuery { From = Now, To = Now.AddDays(-1) }));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeUser : ICurrentUserService
    {
        public Guid? UserId { get; set; }
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => Impersonator;
        public Guid? Impersonator { get; set; }
        public string? Ip { get; set; }
        public string? IpAddress => Ip;
    }

    private sealed class StubTenant : ITenantContext
    {
        public StubTenant(Guid? tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => TenantId is null;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
