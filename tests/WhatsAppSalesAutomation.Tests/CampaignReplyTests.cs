using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Webhooks;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A customer's reply ends campaign automation for them: Responded, no follow-up due.</summary>
public sealed class CampaignReplyTests : IDisposable
{
    private static readonly DateTime RepliedAt = new(2026, 9, 29, 10, 51, 57, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly InboundWebhookProcessor _processor;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Campaign _campaign = new() { Name = "c", Status = CampaignStatus.Running, CreatedBy = Guid.NewGuid() };
    private readonly Customer _harish = new() { PhoneNumberE164 = "+919815733426", FirstName = "Harish", OptInStatus = OptInStatus.OptedIn };
    private readonly Customer _other = new() { PhoneNumberE164 = "+919000000001", FirstName = "Other", OptInStatus = OptInStatus.OptedIn };

    public CampaignReplyTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();

        // Only the campaign-reply step is exercised, and it touches nothing but the context.
        _processor = new InboundWebhookProcessor(_db, null!, null!, null!, null!, null!, null!, NullLogger<InboundWebhookProcessor>.Instance);

        _db.Tenants.Add(_tenant);
        _db.Campaigns.Add(_campaign);
        _db.Customers.AddRange(_harish, _other);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private CampaignCustomer Join(Customer customer, CampaignCustomerStatus status)
    {
        var member = new CampaignCustomer
        {
            CampaignId = _campaign.Id,
            CustomerId = customer.Id,
            Status = status,
            CurrentStepNumber = status == CampaignCustomerStatus.Pending ? -1 : 0,
            NextFollowUpDueAt = status == CampaignCustomerStatus.AwaitingResponse ? RepliedAt.AddDays(2) : null,
        };
        _db.CampaignCustomers.Add(member);
        _db.SaveChanges();
        return member;
    }

    [Fact]
    public async Task A_reply_marks_a_customer_who_was_awaiting_a_follow_up_as_responded()
    {
        var harish = Join(_harish, CampaignCustomerStatus.AwaitingResponse);
        var other = Join(_other, CampaignCustomerStatus.AwaitingResponse);

        await _processor.MarkCampaignRepliesAsync(_harish.Id, isOptOut: false, RepliedAt, default);

        var updated = await _db.CampaignCustomers.SingleAsync(c => c.Id == harish.Id);
        Assert.Equal(CampaignCustomerStatus.Responded, updated.Status);
        Assert.Equal(RepliedAt, updated.LastCustomerResponseAt);
        Assert.Null(updated.NextFollowUpDueAt);
        Assert.Equal(CampaignCustomerStatus.AwaitingResponse, (await _db.CampaignCustomers.SingleAsync(c => c.Id == other.Id)).Status);
    }

    [Fact]
    public async Task A_stop_reply_marks_them_opted_out_instead()
    {
        var harish = Join(_harish, CampaignCustomerStatus.AwaitingResponse);

        await _processor.MarkCampaignRepliesAsync(_harish.Id, isOptOut: true, RepliedAt, default);

        Assert.Equal(CampaignCustomerStatus.OptedOut, (await _db.CampaignCustomers.SingleAsync(c => c.Id == harish.Id)).Status);
    }

    [Fact]
    public async Task A_message_from_someone_never_contacted_changes_nothing()
    {
        var pending = Join(_harish, CampaignCustomerStatus.Pending);

        await _processor.MarkCampaignRepliesAsync(_harish.Id, isOptOut: false, RepliedAt, default);

        var unchanged = await _db.CampaignCustomers.SingleAsync(c => c.Id == pending.Id);
        Assert.Equal(CampaignCustomerStatus.Pending, unchanged.Status);
        Assert.Null(unchanged.LastCustomerResponseAt);
    }

    [Fact]
    public async Task Customers_already_finished_are_left_alone()
    {
        var done = Join(_harish, CampaignCustomerStatus.Completed);

        await _processor.MarkCampaignRepliesAsync(_harish.Id, isOptOut: false, RepliedAt, default);

        Assert.Equal(CampaignCustomerStatus.Completed, (await _db.CampaignCustomers.SingleAsync(c => c.Id == done.Id)).Status);
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
