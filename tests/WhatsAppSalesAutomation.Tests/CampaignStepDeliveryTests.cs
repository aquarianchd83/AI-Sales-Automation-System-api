using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The per-step "who got it / who will / who never will" view behind the campaign detail page.</summary>
public sealed class CampaignStepDeliveryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly CampaignService _service;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Campaign _campaign;
    private readonly Dictionary<string, Customer> _customers = new();
    private readonly Dictionary<string, CampaignCustomer> _members = new();

    public CampaignStepDeliveryTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        // Only the read path is exercised, which touches the context and nothing else.
        _service = new CampaignService(_db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        _campaign = new Campaign { Name = "c", Status = CampaignStatus.Running, CreatedBy = Guid.NewGuid() };
        _campaign.Steps.Add(new CampaignStep { StepNumber = 0, StepType = "Initial", MessageText = "Hi" });
        _campaign.Steps.Add(new CampaignStep { StepNumber = 1, StepType = "FollowUp1", MessageText = "Again" });
        _db.Campaigns.Add(_campaign);

        Member("delivered", CampaignCustomerStatus.AwaitingResponse, currentStep: 0);
        Member("failed", CampaignCustomerStatus.AwaitingResponse, currentStep: -1);
        Member("waiting", CampaignCustomerStatus.Pending, currentStep: -1);
        Member("optedOut", CampaignCustomerStatus.OptedOut, currentStep: -1, stoppedReason: "Customer is not opted in");
        _db.SaveChanges();

        AddMessage("delivered", step: 0, MessageStatus.Delivered);
        AddMessage("failed", step: 0, MessageStatus.Failed, failureReason: "Template rejected");
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Member(string key, CampaignCustomerStatus status, int currentStep, string? stoppedReason = null)
    {
        var customer = new Customer { PhoneNumberE164 = $"+91{9000000000 + _customers.Count}", FirstName = key, OptInStatus = OptInStatus.OptedIn };
        _customers[key] = customer;
        _db.Customers.Add(customer);

        var member = new CampaignCustomer
        {
            CampaignId = _campaign.Id,
            CustomerId = customer.Id,
            Status = status,
            CurrentStepNumber = currentStep,
            StoppedReason = stoppedReason,
            NextFollowUpDueAt = currentStep == 0 ? new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc) : null,
        };
        _members[key] = member;
        _db.CampaignCustomers.Add(member);
    }

    private void AddMessage(string key, int step, MessageStatus status, string? failureReason = null) =>
        _db.Messages.Add(new Message
        {
            CustomerId = _customers[key].Id,
            CampaignCustomerId = _members[key].Id,
            CampaignStepNumber = step,
            IdempotencyKey = $"{_members[key].Id}:{step}",
            Status = status,
            FailureReason = failureReason,
        });

    [Fact]
    public async Task Step_summary_splits_the_audience_into_received_failed_upcoming_and_never()
    {
        var summary = await _service.GetStepDeliverySummaryAsync(_campaign.Id);

        var initial = summary.Single(s => s.StepNumber == 0);
        Assert.Equal(4, initial.Recipients);
        Assert.Equal(1, initial.Delivered);
        Assert.Equal(1, initial.Failed);
        Assert.Equal(1, initial.Upcoming);
        Assert.Equal(1, initial.WillNotReceive);

        // Nobody has been sent the follow-up yet: the delivered one is next in line, the failed and
        // waiting ones are still due it after step 0, and the opted-out one never will be.
        var followUp = summary.Single(s => s.StepNumber == 1);
        Assert.Equal(3, followUp.Upcoming);
        Assert.Equal(1, followUp.WillNotReceive);
        Assert.Equal(0, followUp.Delivered + followUp.Failed + followUp.Sent + followUp.Read + followUp.Queued);
    }

    [Fact]
    public async Task Recipients_list_failures_first_and_carry_the_message_id_to_resend()
    {
        var page = await _service.GetStepRecipientsAsync(_campaign.Id, 0, new CampaignStepRecipientQuery());

        Assert.Equal(4, page.TotalCount);
        var first = page.Items.First();
        Assert.Equal(CampaignStepOutcome.Failed, first.Outcome);
        Assert.NotNull(first.MessageId);
        Assert.Equal("Template rejected", first.FailureReason);

        var never = page.Items.Single(r => r.Outcome == CampaignStepOutcome.WillNotReceive);
        Assert.Equal("Customer is not opted in", never.Note);
        Assert.Null(never.MessageId);
    }

    [Fact]
    public async Task Outcome_filter_narrows_the_list()
    {
        var page = await _service.GetStepRecipientsAsync(_campaign.Id, 0, new CampaignStepRecipientQuery { Outcome = "upcoming" });

        var only = Assert.Single(page.Items);
        Assert.Equal(_customers["waiting"].Id, only.CustomerId);
    }

    [Fact]
    public async Task An_upcoming_follow_up_reports_when_it_is_due()
    {
        var page = await _service.GetStepRecipientsAsync(_campaign.Id, 1, new CampaignStepRecipientQuery { Outcome = "Upcoming" });

        var due = page.Items.Single(r => r.CustomerId == _customers["delivered"].Id);
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), due.DueAt);
    }

    [Fact]
    public async Task An_unknown_step_is_not_found()
    {
        await Assert.ThrowsAsync<WhatsAppSalesAutomation.Application.Common.Exceptions.NotFoundException>(
            () => _service.GetStepRecipientsAsync(_campaign.Id, 9, new CampaignStepRecipientQuery()));
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
