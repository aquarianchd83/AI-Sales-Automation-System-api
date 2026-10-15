using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.LeadDiscovery;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The lead discovery execution end to end on the real model (SQLite): per-customer transactions, the
/// Auto-Campaign steps, step-based retry and idempotency, and the distributed lock - through the real SQL
/// lock store, sharing the test's connection.
/// </summary>
public sealed class LeadDiscoveryExecutionTests : IDisposable
{
    private const string ReferredCampaignName = "Welcome New Leads";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly TestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly SqliteApplicationDbContext _db;
    private readonly ServiceProvider _services;
    private readonly SqlLeadDiscoveryLockStore _sqlLockStore;
    private readonly FakeAgent _agent = new();
    private readonly FakeVerifier _verifier = new();
    private readonly FakeConsent _consent = new();
    private readonly List<Guid> _startedCampaigns = new();
    private readonly LeadDiscoveryOptions _discoveryOptions = new() { LockHeartbeatSeconds = 0, MaxRetryAttempts = 3 };

    private Guid _profileId;
    private Guid _referredCampaignId;
    private Func<Task>? _beforeCampaignCreate;

    public LeadDiscoveryExecutionTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = NewContext(_tenant);
        _db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddScoped<ApplicationDbContext>(_ => NewContext(null));
        _services = services.BuildServiceProvider();
        _sqlLockStore = new SqlLeadDiscoveryLockStore(_services.GetRequiredService<IServiceScopeFactory>(), _clock);

        SeedProfileAndCampaign(autoCampaign: false);
    }

    public void Dispose()
    {
        _db.Dispose();
        _services.Dispose();
        _connection.Dispose();
    }

    private SqliteApplicationDbContext NewContext(Guid? tenantId) =>
        new(_options, new TestTenantContext(tenantId), new AnonymousUser());

    // ---------------------------------------------------------------------------------------------
    // Customers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task New_customers_are_created_once_still_pending_opt_in()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var customers = await _db.Customers.OrderBy(c => c.FirstName).ToListAsync();
        Assert.Equal(new[] { "A Contact", "B Contact" }, customers.Select(c => c.FirstName));
        Assert.All(customers, c =>
        {
            // Being discovered on the web is not consent to be messaged - see
            // LeadDiscoveryRunService.ProcessCustomerAsync's remarks. Campaigns only ever send to
            // OptedIn customers, so nothing discovered here can be messaged until a person actually
            // opts in.
            Assert.Equal(OptInStatus.PendingOptIn, c.OptInStatus);
            Assert.Null(c.OptInTimestamp);
            Assert.Null(c.OptInSource);
            Assert.Equal(_tenant, c.TenantId);
        });

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.Equal(2, execution.CustomersCreated);
        Assert.Equal(new DateTime(2026, 9, 10), execution.ProcessingDate);
    }

    [Fact]
    public async Task Each_new_customer_gets_one_discovered_history_event_for_its_execution()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        _agent.Rounds.Add(Candidates("A"));   // a duplicate found later must not add a second event

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var events = await _db.CustomerHistoryEvents.Where(e => e.EventType == CustomerHistoryEventTypes.Discovered).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(
            (await _db.Customers.Select(c => c.Id).ToListAsync()).OrderBy(i => i),
            events.Select(e => e.CustomerId).OrderBy(i => i));
        Assert.All(events, e =>
        {
            Assert.Equal(CustomerHistoryEventTypes.Discovered, e.EventType);
            Assert.Equal(execution.Id, e.ExecutionId);
            Assert.Equal(_tenant, e.TenantId);
        });
    }

    [Fact]
    public async Task New_customers_are_tagged_with_the_processing_date_and_share_one_tag()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var customers = await _db.Customers.Include(c => c.Tags).ToListAsync();
        Assert.Equal(2, customers.Count);
        Assert.All(customers, c => Assert.Equal(new[] { "2026-09-10" }, c.Tags.Select(t => t.Name)));
        var tag = await _db.CustomerTags.SingleAsync();
        Assert.Equal(_tenant, tag.TenantId);
    }

    [Fact]
    public async Task A_second_run_on_the_same_date_reuses_the_tag_and_customers_already_known_are_not_tagged()
    {
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("A"), FirstName = "Existing A" });
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B"));
        await Service().RunForTenantAsync(_tenant);

        _agent.Rounds.Add(Candidates("C"));
        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(1, await _db.CustomerTags.CountAsync());
        var tagged = await _db.Customers.Where(c => c.Tags.Any()).Select(c => c.FirstName).OrderBy(n => n).ToListAsync();
        Assert.Equal(new[] { "B Contact", "C Contact" }, tagged);
        var existing = await _db.Customers.Include(c => c.Tags).SingleAsync(c => c.FirstName == "Existing A");
        Assert.Empty(existing.Tags);
    }

    [Fact]
    public async Task Another_tenants_tag_of_the_same_name_does_not_block_the_run()
    {
        _db.CustomerTags.Add(new CustomerTag { TenantId = Guid.NewGuid(), Name = "2026-09-10" });
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(1, execution.CustomersCreated);
        Assert.Equal(2, await _db.CustomerTags.IgnoreQueryFilters().CountAsync(t => t.Name == "2026-09-10"));
    }

    [Fact]
    public async Task Discovered_customers_are_never_opted_in_by_discovery()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var customers = await _db.Customers.OrderBy(c => c.FirstName).ToListAsync();
        Assert.Equal(2, customers.Count);
        Assert.All(customers, c =>
        {
            Assert.Equal(OptInStatus.PendingOptIn, c.OptInStatus);
            Assert.Null(c.OptInTimestamp);
            Assert.Null(c.OptInSource);
        });
    }

    [Fact]
    public async Task Quota_exhausted_before_any_candidate_is_still_recorded_in_history()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service(quotaAvailable: 0).RunForTenantAsync(_tenant);

        Assert.Equal(0, await _db.Customers.CountAsync());

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.True(execution.QuotaExhausted);
        Assert.Equal(0, execution.CustomersCreated);
        Assert.Equal(new DateTime(2026, 9, 10), execution.ProcessingDate);
        Assert.Contains("no lead-candidate quota left", execution.Summary);
        Assert.Contains("no lead-candidate quota left", execution.ErrorMessage);
        Assert.False(LeadDiscoveryRetryRules.CanRetry(execution));
        Assert.Equal(0, _agent.Calls);
    }

    [Fact]
    public async Task Quota_exhausted_before_any_candidate_notifies_the_tenant_immediately()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        Guid? notifiedTenant = null;
        QuotaType? notifiedType = null;
        var quotaAlerts = Stub<IQuotaAlertService>.Create(new()
        {
            ["NotifyIfExhaustedAsync"] = args =>
            {
                notifiedTenant = (Guid)args![0]!;
                notifiedType = (QuotaType)args![1]!;
                return Task.FromResult(true);
            }
        });

        await Service(quotaAvailable: 0, quotaAlerts: quotaAlerts).RunForTenantAsync(_tenant);

        // Immediate, not left to the 15-minute quota-alerts sweep - see LeadDiscoveryRunService.NotifyQuotaExhaustedAsync.
        Assert.Equal(_tenant, notifiedTenant);
        Assert.Equal(QuotaType.LeadCandidates, notifiedType);
    }

    [Fact]
    public async Task A_profile_without_keywords_is_still_recorded_in_history()
    {
        var profile = _db.LeadDiscoveryProfiles.Single();
        profile.Keywords = new List<string>();
        await _db.SaveChangesAsync();

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.False(execution.QuotaExhausted);
        Assert.Contains("at least one keyword", execution.ErrorMessage);
        Assert.Equal(LeadDiscoveryCampaignStatus.Skipped, execution.CampaignStatus);
        Assert.False(LeadDiscoveryRetryRules.CanRetry(execution));
        Assert.Equal(0, _agent.Calls);
    }

    [Fact]
    public async Task The_execution_detail_counts_how_the_numbers_came_out_of_the_dnd_check()
    {
        _agent.Rounds.Add(Candidates("A", "B", "C"));
        _verifier.Decide = (id, phone) => phone.EndsWith(Phone("B")[^4..])
            ? new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", null)
            : new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);
        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));
        var detail = await history.GetExecutionAsync(execution.Id);

        var counts = detail.VerificationCounts;
        Assert.Equal(2, counts.Passed);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(0, counts.Pending);
        Assert.Equal(0, counts.Error);
        // The two deliberately flawed simulated candidates (no verifiable phone, closed) were never submitted.
        Assert.Equal(detail.Customers.Count, counts.Passed + counts.Failed + counts.Pending + counts.Error + counts.NotChecked);
    }

    private LeadDiscoveryService LeadsService() =>
        new(_db, null!, null!, null!, null!, null!, NullLogger<LeadDiscoveryService>.Instance);

    private async Task RunWithOneRejectedAsync()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, phone) => phone.EndsWith(Phone("B")[^4..])
            ? new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", "On the DND register")
            : new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);
        await Service().RunForTenantAsync(_tenant);
    }

    [Fact]
    public async Task Discovered_leads_show_batch_check_result_reason_and_campaign_and_keep_the_rejected_one_listed()
    {
        await RunWithOneRejectedAsync();
        var execution = await SingleExecutionAsync();

        var page = await LeadsService().GetDiscoveredLeadsAsync(new PagedRequest { Page = 1, PageSize = 50 });

        var passed = page.Items.Single(l => l.BusinessName.Contains("A"));
        var rejected = page.Items.Single(l => l.BusinessName.Contains("B"));
        Assert.Equal(execution.Id, passed.ExecutionId);
        Assert.Equal(execution.Id, rejected.ExecutionId);

        Assert.Equal("Pass", passed.VerificationStatus);
        Assert.NotNull(passed.VerifiedAtUtc);
        Assert.NotNull(passed.CustomerId);
        Assert.Equal(execution.GeneratedCampaignName, passed.CampaignName);

        // Rejected: still visible for the audit, with its reason, and no customer or campaign.
        Assert.Equal("Fail", rejected.VerificationStatus);
        Assert.Equal("DND", rejected.VerificationReasonCode);
        Assert.Equal("On the DND register", rejected.VerificationReason);
        Assert.Null(rejected.CustomerId);
        Assert.Null(rejected.CampaignName);
    }

    [Fact]
    public async Task Discovered_leads_can_be_filtered_by_verification_status()
    {
        await RunWithOneRejectedAsync();
        var request = new PagedRequest { Page = 1, PageSize = 50 };

        var failed = await LeadsService().GetDiscoveredLeadsAsync(request, null, "Fail");
        var passed = await LeadsService().GetDiscoveredLeadsAsync(request, null, "pass");
        var notChecked = await LeadsService().GetDiscoveredLeadsAsync(request, null, "NotChecked");

        Assert.Equal(new[] { true }, failed.Items.Select(l => l.BusinessName.Contains("B")));
        Assert.Equal(new[] { true }, passed.Items.Select(l => l.BusinessName.Contains("A")));
        Assert.Equal(0, notChecked.TotalCount);
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => LeadsService().GetDiscoveredLeadsAsync(request, null, "Maybe"));
    }

    [Fact]
    public async Task A_lead_found_with_the_check_off_is_not_checked_and_filters_as_such()
    {
        EnableAutoCampaign();
        var profile = await _db.LeadDiscoveryProfiles.SingleAsync();
        profile.DndCheckEnabled = false;
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);

        var page = await LeadsService().GetDiscoveredLeadsAsync(new PagedRequest { Page = 1, PageSize = 50 }, null, "NotChecked");

        Assert.Single(page.Items);
        Assert.Equal("NotChecked", page.Items[0].VerificationStatus);
    }

    [Fact]
    public async Task The_execution_detail_names_the_campaign_only_on_rows_whose_customer_joined_it()
    {
        await RunWithOneRejectedAsync();
        var execution = await SingleExecutionAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));

        var detail = await history.GetExecutionAsync(execution.Id);

        Assert.Equal(execution.GeneratedCampaignName, detail.Customers.Single(c => c.CustomerName.Contains("A")).CampaignName);
        Assert.Null(detail.Customers.Single(c => c.CustomerName.Contains("B")).CampaignName);
    }

    [Fact]
    public async Task With_the_consent_sms_on_only_the_customers_the_campaign_took_are_invited()
    {
        EnableAutoCampaign();
        var profile = await _db.LeadDiscoveryProfiles.SingleAsync();
        profile.ConsentSmsEnabled = true;
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, phone) => phone.EndsWith(Phone("B")[^4..])
            ? new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", null)
            : new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var call = Assert.Single(_consent.Sent);
        Assert.Equal(execution.GeneratedCampaignId, call.CampaignId);
        Assert.Equal(execution.Id, call.ExecutionId);
        var passed = await _db.Customers.SingleAsync(c => c.FirstName == "A Contact");
        Assert.Equal(new[] { passed.Id }, call.CustomerIds);   // the DND-rejected number has no customer and is never texted
        Assert.Equal(1, _consent.RetryCalls);                  // failed invitations are retried whenever the job runs
    }

    [Fact]
    public async Task With_the_consent_sms_off_nothing_is_sent_and_nothing_is_retried()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Empty(_consent.Sent);
        Assert.Equal(0, _consent.RetryCalls);
    }

    [Fact]
    public async Task A_problem_sending_the_invitations_does_not_fail_the_execution()
    {
        EnableAutoCampaign();
        var profile = await _db.LeadDiscoveryProfiles.SingleAsync();
        profile.ConsentSmsEnabled = true;
        await _db.SaveChangesAsync();
        _consent.ThrowOnSend = true;
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.Equal(LeadDiscoveryCampaignStatus.Created, execution.CampaignStatus);
    }

    [Fact]
    public async Task The_execution_detail_shows_the_name_of_each_customer_created()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, phone) => phone.EndsWith(Phone("B")[^4..])
            ? new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", null)
            : new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);
        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));
        var detail = await history.GetExecutionAsync(execution.Id);

        Assert.Equal("A Contact", detail.Customers.Single(c => c.CustomerName.Contains("A")).CrmCustomerName);
        Assert.Null(detail.Customers.Single(c => c.CustomerName.Contains("B")).CrmCustomerName);   // rejected: no customer
    }

    [Fact]
    public async Task A_run_that_researched_is_linked_to_its_execution_and_shown_on_its_detail()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var run = await _db.LeadDiscoveryRuns.AsNoTracking().SingleAsync();
        Assert.Equal(execution.Id, run.ExecutionId);

        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));
        var detail = await history.GetExecutionAsync(execution.Id);

        Assert.NotNull(detail.Research);
        Assert.Equal(execution.Id, detail.Research!.ExecutionId);
        Assert.Equal("Simulated", detail.Research.Model);
        Assert.Equal(2, detail.Research.CandidatesConsidered);
        Assert.Equal(2, detail.Research.LeadsSaved);
        // A simulated run is free, so its per-lead cost is zero rather than a division by the lead count failing.
        Assert.Equal(0m, detail.Research.CostPerLeadUsd);
        Assert.False(string.IsNullOrEmpty(detail.CurrencyCode));
        // The list row carries the same cost as the detail's Research, so History doesn't need a second call.
        Assert.Equal(detail.Research.EstimatedCostLocal, detail.Execution.EstimatedCostLocal);
        Assert.Equal(detail.CurrencyCode, detail.Execution.CurrencyCode);

        var page = await history.GetHistoryAsync(new LeadDiscoveryHistoryQuery());
        var row = Assert.Single(Assert.Single(page.Items).Executions);
        Assert.Equal(detail.Execution.EstimatedCostLocal, row.EstimatedCostLocal);
        Assert.Equal(detail.CurrencyCode, row.CurrencyCode);
    }

    [Fact]
    public async Task A_skipped_run_has_no_research_on_its_detail()
    {
        await Service(quotaAvailable: 0).RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));

        var detail = await history.GetExecutionAsync(execution.Id);
        Assert.Null(detail.Research);
        Assert.Equal(0m, detail.Execution.EstimatedCostLocal);
    }

    [Fact]
    public async Task Existing_customers_are_duplicates_and_nothing_is_inserted()
    {
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("A"), FirstName = "Already here" });
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(2, await _db.Customers.CountAsync());
        var execution = await SingleExecutionAsync();
        Assert.Equal(1, execution.CustomersCreated);
        Assert.Equal(1, execution.CustomersDuplicate);
    }

    [Fact]
    public async Task One_failed_customer_does_not_roll_back_the_others()
    {
        _agent.Rounds.Add(Candidates("A", "B", "C", "D"));
        FailCustomerSave("C Contact");

        await Service().RunForTenantAsync(_tenant);

        var names = await _db.Customers.Select(c => c.FirstName).OrderBy(n => n).ToListAsync();
        Assert.Equal(new[] { "A Contact", "B Contact", "D Contact" }, names);
        Assert.Equal(3, await _db.DiscoveredLeads.CountAsync());

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.RetryPending, execution.Status);
        Assert.Equal(3, execution.CustomersCreated);
        Assert.Equal(1, execution.CustomersFailed);

        var failed = await _db.LeadDiscoveryExecutionCustomers.SingleAsync(r => r.Status == LeadDiscoveryCustomerStatus.Failed);
        Assert.Equal("C", failed.CustomerName);
        Assert.NotNull(failed.CandidateJson);
        Assert.Contains("Simulated failure", failed.ErrorMessage);
    }

    // ---------------------------------------------------------------------------------------------
    // Auto-Campaign
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Auto_campaign_not_configured_skips_campaign_processing_cleanly()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.False(execution.AutoCampaignConfigured);
        Assert.Equal(LeadDiscoveryCampaignStatus.Skipped, execution.CampaignStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Skipped, execution.TemplateStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Skipped, execution.MappingStatus);
        Assert.Equal("Auto-Campaign = Not Configured", execution.CampaignNote);
        Assert.Equal(1, await _db.Campaigns.CountAsync()); // only the referred campaign
    }

    [Fact]
    public async Task Auto_campaign_creates_one_named_campaign_with_the_configured_templates_and_new_customers()
    {
        EnableAutoCampaign();
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("Z"), FirstName = "Existing" });
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B", "Z"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.Equal(LeadDiscoveryCampaignStatus.Created, execution.CampaignStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Completed, execution.TemplateStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Completed, execution.MappingStatus);

        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal($"{ReferredCampaignName} - 2026-09-10", generated.Name);
        Assert.Equal(generated.Id, execution.GeneratedCampaignId);
        // Immediate is the profile's default start mode - the referred campaign's own schedule (set in
        // SeedProfileAndCampaign) is never consulted.
        Assert.Null(generated.ScheduledStartAt);

        // Templates: exactly the referred campaign's steps, in order, with their configuration.
        var steps = await _db.CampaignSteps.Where(s => s.CampaignId == generated.Id).OrderBy(s => s.StepNumber).ToListAsync();
        var sourceSteps = await _db.CampaignSteps.Where(s => s.CampaignId == _referredCampaignId).OrderBy(s => s.StepNumber).ToListAsync();
        Assert.Equal(sourceSteps.Select(s => (s.StepNumber, s.MessageTemplateId, s.DelayDaysAfterPrevious, s.MessageText)),
            steps.Select(s => (s.StepNumber, s.MessageTemplateId, s.DelayDaysAfterPrevious, s.MessageText)));
        Assert.DoesNotContain(steps, s => s.MessageTemplateId == UnrelatedTemplateId);

        // Only the newly created customers - not the existing duplicate.
        var mapped = await _db.CampaignCustomers.Where(cc => cc.CampaignId == generated.Id).Select(cc => cc.CustomerId).ToListAsync();
        var created = await _db.Customers.Where(c => c.FirstName != "Existing").Select(c => c.Id).ToListAsync();
        Assert.Equal(created.OrderBy(x => x), mapped.OrderBy(x => x));

        Assert.Equal(new[] { generated.Id }, _startedCampaigns);
    }

    [Fact]
    public async Task Auto_campaign_with_next_day_start_is_scheduled_the_day_after_processing_date()
    {
        EnableAutoCampaign();
        EnableNextDayCampaignStart(new TimeSpan(9, 30, 0));
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal(new DateTime(2026, 9, 11, 9, 30, 0), generated.ScheduledStartAt);
    }

    [Fact]
    public async Task Zero_new_customers_skips_the_campaign()
    {
        EnableAutoCampaign();
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("A"), FirstName = "Existing A" });
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("B"), FirstName = "Existing B" });
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.Equal(LeadDiscoveryCampaignStatus.Skipped, execution.CampaignStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Skipped, execution.TemplateStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Skipped, execution.MappingStatus);
        Assert.Equal("No new customers discovered/created. Campaign creation skipped.", execution.CampaignNote);
        Assert.Equal(1, await _db.Campaigns.CountAsync());
    }

    [Fact]
    public async Task A_second_run_on_the_same_processing_date_reuses_the_campaign()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);

        _agent.Rounds.Add(Candidates("B"));
        await Service().RunForTenantAsync(_tenant);

        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal(2, await _db.CampaignCustomers.CountAsync(cc => cc.CampaignId == generated.Id));
        Assert.Equal(3, await _db.CampaignSteps.CountAsync(s => s.CampaignId == generated.Id));
        Assert.Equal(1, await _db.LeadDiscoveryGeneratedCampaigns.CountAsync());
    }

    [Fact]
    public async Task A_deleted_generated_campaign_is_replaced_by_a_new_one_on_the_next_run()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);

        var deleted = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        _db.CampaignCustomers.RemoveRange(_db.CampaignCustomers.Where(cc => cc.CampaignId == deleted.Id));
        _db.CampaignSteps.RemoveRange(_db.CampaignSteps.Where(s => s.CampaignId == deleted.Id));
        _db.Campaigns.Remove(deleted);
        await _db.SaveChangesAsync();

        _agent.Rounds.Add(Candidates("B"));
        await Service().RunForTenantAsync(_tenant);

        var replacement = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.NotEqual(deleted.Id, replacement.Id);
        var mapping = await _db.LeadDiscoveryGeneratedCampaigns.SingleAsync();
        Assert.Equal(replacement.Id, mapping.CampaignId);
        var latest = (await _db.LeadDiscoveryExecutions.ToListAsync()).Last();
        Assert.Equal(LeadDiscoveryCampaignStatus.Created, latest.CampaignStatus);
    }

    [Fact]
    public async Task A_retired_generated_campaign_is_not_reused_and_a_new_one_is_created()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);

        var retired = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        retired.RetiredAt = DateTime.UtcNow;
        retired.RetiredReason = "Wrong audience";
        await _db.SaveChangesAsync();

        _agent.Rounds.Add(Candidates("B"));
        await Service().RunForTenantAsync(_tenant);

        var replacement = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId && c.RetiredAt == null);
        Assert.NotEqual(retired.Id, replacement.Id);
        Assert.Equal(1, await _db.CampaignCustomers.CountAsync(cc => cc.CampaignId == replacement.Id));
        Assert.Equal(1, await _db.CampaignCustomers.CountAsync(cc => cc.CampaignId == retired.Id));
        Assert.Equal(replacement.Id, (await _db.LeadDiscoveryGeneratedCampaigns.SingleAsync()).CampaignId);
    }

    [Fact]
    public async Task The_database_rejects_a_second_campaign_for_the_same_logical_key()
    {
        var key = new LeadDiscoveryGeneratedCampaign
        {
            TenantId = _tenant, LeadDiscoveryProfileId = _profileId, AutoCampaignId = _referredCampaignId,
            ProcessingDate = new DateTime(2026, 9, 10), CampaignId = Guid.NewGuid(), ExecutionId = Guid.NewGuid()
        };
        _db.LeadDiscoveryGeneratedCampaigns.Add(key);
        await _db.SaveChangesAsync();

        _db.LeadDiscoveryGeneratedCampaigns.Add(new LeadDiscoveryGeneratedCampaign
        {
            TenantId = _tenant, LeadDiscoveryProfileId = _profileId, AutoCampaignId = _referredCampaignId,
            ProcessingDate = new DateTime(2026, 9, 10), CampaignId = Guid.NewGuid(), ExecutionId = Guid.NewGuid()
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // Retry
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_resumes_only_the_failed_customer_and_the_missing_campaign()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A", "B", "C", "D"));
        var failCustomer = FailCustomerSave("C Contact");
        var campaignAttempts = 0;
        _beforeCampaignCreate = () => ++campaignAttempts == 1 ? throw new InvalidOperationException("Campaign store down") : Task.CompletedTask;

        await Service().RunForTenantAsync(_tenant);

        var first = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.RetryPending, first.Status);
        Assert.Equal(LeadDiscoveryCampaignStatus.RetryPending, first.CampaignStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Pending, first.TemplateStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.Pending, first.MappingStatus);
        Assert.Equal(3, await _db.Customers.CountAsync()); // A, B, D kept
        Assert.Equal(1, await _db.Campaigns.CountAsync());

        failCustomer.Enabled = false;
        var summary = await Service().RetryExecutionAsync(_tenant, first.Id);
        Assert.DoesNotContain("Skipped:", summary);

        var executions = await Executions();
        var retry = executions.Single(e => e.Id != first.Id);
        var original = executions.Single(e => e.Id == first.Id);

        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, retry.Status);
        Assert.Equal(first.Id, retry.RetryOfExecutionId);
        Assert.Equal(first.RootExecutionId, retry.RootExecutionId);
        Assert.Equal(1, retry.RetryCount);
        Assert.Equal(1, retry.CustomersCreated);    // C
        Assert.Equal(3, retry.CustomersSkipped);    // A, B, D already exist
        Assert.Equal(retry.Id, original.SupersededByExecutionId);

        Assert.Equal(4, await _db.Customers.CountAsync());
        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal(4, await _db.CampaignCustomers.CountAsync(cc => cc.CampaignId == generated.Id));
        Assert.Equal(3, await _db.CampaignSteps.CountAsync(s => s.CampaignId == generated.Id));

        // A superseded execution cannot be retried again.
        Assert.StartsWith("Skipped:", await Service().RetryExecutionAsync(_tenant, first.Id));
    }

    [Fact]
    public async Task Template_failure_retries_only_the_missing_templates()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A"));
        var failStep = new SaveFailure(ctx => ctx.ChangeTracker.Entries<CampaignStep>()
            .Any(e => e.State == EntityState.Added && e.Entity.StepNumber == 1 && e.Entity.CampaignId != _referredCampaignId));
        _db.BeforeSave += failStep.Apply;

        await Service().RunForTenantAsync(_tenant);

        var first = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryCampaignStatus.Created, first.CampaignStatus);
        Assert.Equal(LeadDiscoveryAssociationStatus.RetryPending, first.TemplateStatus);
        var templateRows = await _db.LeadDiscoveryExecutionTemplates.Where(t => t.ExecutionId == first.Id).OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal(new[] { LeadDiscoveryAssociationStatus.Completed, LeadDiscoveryAssociationStatus.Failed, LeadDiscoveryAssociationStatus.Pending },
            templateRows.Select(t => t.Status));
        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal(1, await _db.CampaignSteps.CountAsync(s => s.CampaignId == generated.Id));

        failStep.Enabled = false;
        await Service().RetryExecutionAsync(_tenant, first.Id);

        var retry = (await Executions()).Single(e => e.Id != first.Id);
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, retry.Status);
        Assert.Equal(generated.Id, retry.GeneratedCampaignId); // not recreated
        Assert.Equal(1, await _db.Campaigns.CountAsync(c => c.Id != _referredCampaignId));
        var retryRows = await _db.LeadDiscoveryExecutionTemplates.Where(t => t.ExecutionId == retry.Id).OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal("Already associated - skipped.", retryRows[0].ErrorMessage);
        Assert.All(retryRows, r => Assert.Equal(LeadDiscoveryAssociationStatus.Completed, r.Status));
        Assert.Equal(3, await _db.CampaignSteps.CountAsync(s => s.CampaignId == generated.Id));
    }

    [Fact]
    public async Task Scheduled_runs_retry_pending_executions_automatically_until_the_limit()
    {
        _discoveryOptions.MaxRetryAttempts = 2;
        _agent.Rounds.Add(Candidates("A", "C"));
        FailCustomerSave("C Contact");

        await Service().RunForTenantAsync(_tenant); // E1: C fails -> RetryPending
        await Service().RunForTenantAsync(_tenant); // retry 1 (fails again) + fresh run
        await Service().RunForTenantAsync(_tenant); // retry 2 (fails again, limit reached) + fresh run
        await Service().RunForTenantAsync(_tenant); // nothing left to retry

        var chain = (await Executions()).Where(e => e.CustomersDiscovered > 0).OrderBy(e => e.RetryCount).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, chain.Select(e => e.RetryCount));
        Assert.Equal(LeadDiscoveryExecutionStatus.PartiallyCompleted, chain[^1].Status);
        Assert.Contains("exhausted", chain[^1].NextRetryInfo);
        Assert.All(chain.Skip(1), e => Assert.Equal(LeadDiscoveryTriggers.AutomaticRetry, e.Trigger));
    }

    // ---------------------------------------------------------------------------------------------
    // Locking
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_execution_blocked_by_the_lock_does_nothing_and_is_never_released()
    {
        var holder = Claim(_tenant, _profileId);
        Assert.True((await _sqlLockStore.TryAcquireAsync(holder, TimeSpan.FromMinutes(10))).Acquired);
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Failed, execution.Status);
        Assert.Equal(LeadDiscoveryLockStatus.Blocked, execution.LockStatus);
        Assert.Equal(LeadDiscoverySteps.LockAcquisition, execution.FailedStep);
        Assert.Contains(holder.ExecutionId.ToString(), execution.ErrorMessage);
        Assert.Equal(0, _agent.Calls);
        Assert.Equal(0, await _db.Customers.CountAsync());

        var transitions = await TransitionsAsync(execution.Id);
        Assert.Equal(new[] { "Pending>Acquiring", "Acquiring>Blocked" }, transitions);
        Assert.False(LeadDiscoveryRetryRules.CanRetry(execution));
    }

    [Fact]
    public async Task A_completed_execution_records_the_full_lock_lifecycle()
    {
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryLockStatus.Released, execution.LockStatus);
        var transitions = await TransitionsAsync(execution.Id);
        Assert.Equal(new[] { "Pending>Acquiring", "Acquiring>Acquired" }, transitions.Take(2));
        Assert.Equal(new[] { "Acquired>ReleasePending", "ReleasePending>Released" }, transitions.TakeLast(2));
        Assert.All(transitions.Skip(2).SkipLast(2), t => Assert.True(t is "Acquired>Renewing" or "Renewing>Acquired", t));

        // Released: the next execution can take the lock straight away.
        Assert.True((await _sqlLockStore.TryAcquireAsync(Claim(_tenant, _profileId), TimeSpan.FromMinutes(1))).Acquired);
    }

    [Fact]
    public async Task Losing_the_lock_stops_new_work_but_keeps_committed_customers()
    {
        // Renew at every checkpoint, and have the provider report the lock lost on the fourth renewal:
        // round checkpoint (1), verification (2), customer A (3), customer B (4 -> lost).
        _discoveryOptions.LockRenewWhenRemainingSeconds = _discoveryOptions.LockLeaseSeconds;
        var store = new ScriptedLockStore(_sqlLockStore) { LoseOnRenewal = 4 };
        _agent.Rounds.Add(Candidates("A", "B", "C"));

        await Service(store).RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryLockStatus.Lost, execution.LockStatus);
        Assert.Equal(LeadDiscoveryExecutionStatus.RetryPending, execution.Status);
        Assert.Equal(new[] { "A Contact" }, await _db.Customers.Select(c => c.FirstName).ToListAsync());
        Assert.Equal(2, await _db.LeadDiscoveryExecutionCustomers.CountAsync(r => r.Status == LeadDiscoveryCustomerStatus.Pending));
        var transitions = await TransitionsAsync(execution.Id);
        Assert.Contains("Renewing>Lost", transitions);
        Assert.DoesNotContain(transitions, t => t.Contains("Release"));

        // The lost execution never released, so its lease has to run out first. Then a new execution (new id,
        // new token) picks up B and C; A is not created twice.
        _clock.UtcNow = _clock.UtcNow.AddSeconds(_discoveryOptions.LockLeaseSeconds + 1);
        await Service().RetryExecutionAsync(_tenant, execution.Id);
        Assert.Equal(3, await _db.Customers.CountAsync());
        var retry = (await Executions()).Single(e => e.Id != execution.Id);
        Assert.Equal(LeadDiscoveryLockStatus.Released, retry.LockStatus);
        Assert.NotEqual(execution.LockTokenReference, retry.LockTokenReference);
    }

    [Fact]
    public async Task An_expired_lease_stops_the_execution()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        var advanced = false;
        _db.BeforeSave += ctx =>
        {
            if (!advanced && ctx.ChangeTracker.Entries<Customer>().Any(e => e.State == EntityState.Added))
            {
                advanced = true;
                _clock.UtcNow = _clock.UtcNow.AddSeconds(_discoveryOptions.LockLeaseSeconds + 1);
            }
        };

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryLockStatus.Expired, execution.LockStatus);
        Assert.Equal(LeadDiscoveryExecutionStatus.RetryPending, execution.Status);
        Assert.Equal(1, await _db.Customers.CountAsync());
        Assert.Contains("Acquired>Expired", await TransitionsAsync(execution.Id));
    }

    [Fact]
    public async Task A_stale_running_execution_is_expired_and_made_retryable_by_the_next_one()
    {
        var staleId = Guid.NewGuid();
        _db.LeadDiscoveryExecutions.Add(new LeadDiscoveryExecution
        {
            Id = staleId, RootExecutionId = staleId, TenantId = _tenant, LeadDiscoveryProfileId = _profileId,
            ProfileName = "x", ProcessingDate = new DateTime(2026, 9, 9), Trigger = LeadDiscoveryTriggers.Scheduled,
            StartedAtUtc = _clock.UtcNow.AddDays(-1), Status = LeadDiscoveryExecutionStatus.Processing,
            LockStatus = LeadDiscoveryLockStatus.Acquired, LockKey = "k"
        });
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A"));

        await Service().RunForTenantAsync(_tenant);

        var stale = await _db.LeadDiscoveryExecutions.AsNoTracking().SingleAsync(e => e.Id == staleId);
        Assert.Equal(LeadDiscoveryLockStatus.Expired, stale.LockStatus);
        Assert.Equal(LeadDiscoveryExecutionStatus.RetryPending, stale.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Lock store and state machine
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Same_tenant_and_profile_is_exclusive_but_other_profiles_and_tenants_are_not()
    {
        var lease = TimeSpan.FromMinutes(5);
        var otherTenant = Guid.NewGuid();
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();

        Assert.True((await _sqlLockStore.TryAcquireAsync(Claim(_tenant, p1), lease)).Acquired);
        Assert.False((await _sqlLockStore.TryAcquireAsync(Claim(_tenant, p1), lease)).Acquired);
        Assert.True((await _sqlLockStore.TryAcquireAsync(Claim(_tenant, p2), lease)).Acquired);
        Assert.True((await _sqlLockStore.TryAcquireAsync(Claim(otherTenant, p1), lease)).Acquired);
    }

    [Fact]
    public async Task Only_the_token_holder_can_renew_or_release_and_an_old_owner_cannot_touch_a_newer_lock()
    {
        var lease = TimeSpan.FromMinutes(5);
        var first = Claim(_tenant, _profileId);
        Assert.True((await _sqlLockStore.TryAcquireAsync(first, lease)).Acquired);

        var impostor = first with { LockToken = Guid.NewGuid() };
        Assert.Equal(LeadDiscoveryLockOwnership.Lost, (await _sqlLockStore.RenewAsync(impostor, lease)).Ownership);
        Assert.Equal(LeadDiscoveryLockOwnership.Lost, (await _sqlLockStore.ReleaseAsync(impostor)).Ownership);
        Assert.Equal(LeadDiscoveryLockOwnership.Owned, (await _sqlLockStore.RenewAsync(first, lease)).Ownership);

        // The first owner's lease runs out and a newer execution takes the lock.
        _clock.UtcNow = _clock.UtcNow.AddMinutes(6);
        Assert.Equal(LeadDiscoveryLockOwnership.Expired, (await _sqlLockStore.RenewAsync(first, lease)).Ownership);
        var second = Claim(_tenant, _profileId);
        Assert.True((await _sqlLockStore.TryAcquireAsync(second, lease)).Acquired);

        Assert.Equal(LeadDiscoveryLockOwnership.Lost, (await _sqlLockStore.RenewAsync(first, lease)).Ownership);
        Assert.Equal(LeadDiscoveryLockOwnership.Lost, (await _sqlLockStore.ReleaseAsync(first)).Ownership);
        Assert.False((await _sqlLockStore.TryAcquireAsync(Claim(_tenant, _profileId), lease)).Acquired);
        Assert.Equal(LeadDiscoveryLockOwnership.Owned, (await _sqlLockStore.ReleaseAsync(second)).Ownership);
    }

    [Fact]
    public async Task An_expired_token_is_never_used_to_release()
    {
        var claim = Claim(_tenant, _profileId);
        await _sqlLockStore.TryAcquireAsync(claim, TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Equal(LeadDiscoveryLockOwnership.Expired, (await _sqlLockStore.ReleaseAsync(claim)).Ownership);
        using var scope = _services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LeadDiscoveryLocks.SingleAsync();
        Assert.Equal(LeadDiscoveryLockStatus.Acquired, row.Status); // untouched; it is simply past its lease
    }

    [Theory]
    [InlineData(LeadDiscoveryLockStatus.Blocked, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Blocked, LeadDiscoveryLockStatus.Renewing)]
    [InlineData(LeadDiscoveryLockStatus.Blocked, LeadDiscoveryLockStatus.Released)]
    [InlineData(LeadDiscoveryLockStatus.Released, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Released, LeadDiscoveryLockStatus.Renewing)]
    [InlineData(LeadDiscoveryLockStatus.Released, LeadDiscoveryLockStatus.ReleasePending)]
    [InlineData(LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.Renewing)]
    [InlineData(LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.ReleasePending)]
    [InlineData(LeadDiscoveryLockStatus.Lost, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Lost, LeadDiscoveryLockStatus.Renewing)]
    [InlineData(LeadDiscoveryLockStatus.Lost, LeadDiscoveryLockStatus.ReleasePending)]
    [InlineData(LeadDiscoveryLockStatus.ReleasePending, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.ReleasePending)]
    [InlineData(LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.Released)]
    [InlineData(LeadDiscoveryLockStatus.Pending, LeadDiscoveryLockStatus.Acquired)]
    public void Invalid_lock_transitions_are_rejected(LeadDiscoveryLockStatus from, LeadDiscoveryLockStatus to) =>
        Assert.False(LeadDiscoveryLockStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(LeadDiscoveryLockStatus.Pending, LeadDiscoveryLockStatus.Acquiring)]
    [InlineData(LeadDiscoveryLockStatus.Acquiring, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Acquiring, LeadDiscoveryLockStatus.Blocked)]
    [InlineData(LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStatus.Renewing)]
    [InlineData(LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.Acquired)]
    [InlineData(LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.RenewalFailed)]
    [InlineData(LeadDiscoveryLockStatus.RenewalFailed, LeadDiscoveryLockStatus.Renewing)]
    [InlineData(LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.Expired)]
    [InlineData(LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.Lost)]
    [InlineData(LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStatus.Expired)]
    [InlineData(LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStatus.Lost)]
    [InlineData(LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStatus.ReleasePending)]
    [InlineData(LeadDiscoveryLockStatus.ReleasePending, LeadDiscoveryLockStatus.ReleasePending)]
    [InlineData(LeadDiscoveryLockStatus.ReleasePending, LeadDiscoveryLockStatus.Released)]
    [InlineData(LeadDiscoveryLockStatus.ReleasePending, LeadDiscoveryLockStatus.Expired)]
    public void Valid_lock_transitions_are_allowed(LeadDiscoveryLockStatus from, LeadDiscoveryLockStatus to) =>
        Assert.True(LeadDiscoveryLockStateMachine.CanTransition(from, to));

    [Fact]
    public void Only_acquired_allows_business_processing_and_terminal_states_are_final()
    {
        foreach (var status in Enum.GetValues<LeadDiscoveryLockStatus>())
        {
            Assert.Equal(status == LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStateMachine.AllowsBusinessProcessing(status));

            var terminal = status is LeadDiscoveryLockStatus.Blocked or LeadDiscoveryLockStatus.Released
                or LeadDiscoveryLockStatus.Expired or LeadDiscoveryLockStatus.Lost;
            Assert.Equal(terminal, LeadDiscoveryLockStateMachine.IsTerminal(status));
            if (terminal)
                Assert.All(Enum.GetValues<LeadDiscoveryLockStatus>(), to => Assert.False(LeadDiscoveryLockStateMachine.CanTransition(status, to)));
        }
    }

    [Fact]
    public void Campaign_name_is_the_referred_name_and_processing_date()
    {
        Assert.Equal("Welcome New Leads - 2026-09-26", LeadDiscoveryRunService.BuildCampaignName("Welcome New Leads", new DateTime(2026, 9, 26)));
        Assert.Equal(200, LeadDiscoveryRunService.BuildCampaignName(new string('x', 300), new DateTime(2026, 9, 26)).Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Setup
    // ---------------------------------------------------------------------------------------------

    private static readonly Guid UnrelatedTemplateId = Guid.NewGuid();

    private void SeedProfileAndCampaign(bool autoCampaign)
    {
        var templates = Enumerable.Range(0, 3).Select(i => new MessageTemplate
        {
            TenantId = _tenant, Name = $"Template {i}", WhatsAppTemplateName = $"template_{i}", BodyText = "Hi {{FirstName}}"
        }).ToList();
        _db.MessageTemplates.AddRange(templates);
        _db.MessageTemplates.Add(new MessageTemplate
        {
            Id = UnrelatedTemplateId, TenantId = _tenant, Name = "Unrelated", WhatsAppTemplateName = "unrelated", BodyText = "x"
        });

        var campaign = new Campaign
        {
            TenantId = _tenant, Name = ReferredCampaignName, Status = CampaignStatus.Running, CreatedBy = Guid.NewGuid(),
            ScheduledStartAt = new DateTime(2026, 1, 1, 10, 30, 0)
        };
        for (var i = 0; i < 3; i++)
        {
            campaign.Steps.Add(new CampaignStep
            {
                TenantId = _tenant, CampaignId = campaign.Id, StepNumber = i,
                StepType = i == 0 ? CampaignStepTypeName.Initial : $"FollowUp{i}",
                DelayDaysAfterPrevious = i * 2, MessageText = $"Step {i} for {{{{FirstName}}}}", MessageTemplateId = templates[i].Id
            });
        }
        _db.Campaigns.Add(campaign);
        _referredCampaignId = campaign.Id;

        var profile = new LeadDiscoveryProfile
        {
            TenantId = _tenant, IsEnabled = true, TargetBusinessType = "Eye clinic",
            Keywords = new List<string> { "eye clinic" }, Locations = new List<string> { "Chandigarh" },
            BatchSize = 25, PhoneRequired = true, MinimumLeadScore = 60, DndCheckEnabled = true,
            AutoCampaignEnabled = autoCampaign, SourceCampaignId = autoCampaign ? campaign.Id : null
        };
        _db.LeadDiscoveryProfiles.Add(profile);
        _profileId = profile.Id;
        _db.SaveChanges();
    }

    private void EnableAutoCampaign()
    {
        var profile = _db.LeadDiscoveryProfiles.Single();
        profile.AutoCampaignEnabled = true;
        profile.SourceCampaignId = _referredCampaignId;
        _db.SaveChanges();
    }

    private void EnableNextDayCampaignStart(TimeSpan time)
    {
        var profile = _db.LeadDiscoveryProfiles.Single();
        profile.AutoCampaignStartMode = LeadDiscoveryCampaignStartMode.NextDayWithTime;
        profile.AutoCampaignStartTime = time;
        _db.SaveChanges();
    }

    private LeadDiscoveryRunService Service(ILeadDiscoveryLockStore? lockStore = null, decimal quotaAvailable = 1000m, IQuotaAlertService? quotaAlerts = null)
    {
        var planLimits = Stub<IPlanLimitsService>.Create(new()
        {
            ["GetLeadDiscoveryBatchLimitAsync"] = _ => Task.FromResult<int?>(null),
            ["EnsureCanCreateCampaignAsync"] = _ => _beforeCampaignCreate?.Invoke() ?? Task.CompletedTask
        });
        var quota = Stub<IQuotaGate>.Create(new()
        {
            ["GetAvailableAsync"] = _ => Task.FromResult(quotaAvailable),
            ["ConsumeLeadCandidatesAsync"] = args => Task.FromResult((decimal)args![1]!)
        });
        var campaigns = Stub<ICampaignService>.Create(new()
        {
            ["StartAsync"] = args =>
            {
                _startedCampaigns.Add((Guid)args![0]!);
                return Task.FromResult<CampaignDto>(null!);
            }
        });

        return new LeadDiscoveryRunService(
            _db, _agent, planLimits, quota, quotaAlerts ?? Stub<IQuotaAlertService>.Create(new()),
            _clock, new IstTimeZone(_clock), campaigns,
            lockStore ?? _sqlLockStore, new ApplicationInstance(), NullLogger<LeadDiscoveryRunService>.Instance,
            Options.Create(_discoveryOptions), new Snapshot<LeadDiscoveryPricingOptions>(new LeadDiscoveryPricingOptions()), _verifier, _consent);
    }

    private SaveFailure FailCustomerSave(string firstName)
    {
        var failure = new SaveFailure(ctx => ctx.ChangeTracker.Entries<Customer>()
            .Any(e => e.State == EntityState.Added && e.Entity.FirstName == firstName));
        _db.BeforeSave += failure.Apply;
        return failure;
    }

    private async Task<LeadDiscoveryExecution> SingleExecutionAsync() => (await Executions()).Single();

    private Task<List<LeadDiscoveryExecution>> Executions() =>
        _db.LeadDiscoveryExecutions.AsNoTracking().OrderBy(e => e.StartedAtUtc).ThenBy(e => e.RetryCount).ToListAsync();

    private async Task<List<string>> TransitionsAsync(Guid executionId) =>
        (await _db.LeadDiscoveryLockTransitions.AsNoTracking()
            .Where(t => t.ExecutionId == executionId)
            .OrderBy(t => t.TransitionAtUtc).ThenBy(t => t.CreatedAt)
            .ToListAsync())
        .Select(t => $"{t.FromStatus}>{t.ToStatus}")
        .ToList();

    private static LeadDiscoveryLockClaim Claim(Guid tenantId, Guid profileId) =>
        new(tenantId, profileId, Guid.NewGuid(), Guid.NewGuid(), "test-instance");

    private static string Phone(string name) => $"+9198765{(name[0] - 'A'):D5}";

    /// <summary>Qualifying candidates, each with a phone that appears on a fetched page.</summary>
    private static LeadDiscoveryAgentResult Candidates(params string[] names)
    {
        var pages = new List<FetchedPage>();
        var seen = new List<string>();
        var candidates = names.Select(name =>
        {
            var url = $"https://{name.ToLowerInvariant()}-clinic.example/contact";
            pages.Add(new FetchedPage(url, $"{name} clinic. Call {Phone(name)}."));
            seen.Add(url);
            return new DiscoveredBusinessCandidate(name, "Eye clinic", $"{name} Contact", null, "Chandigarh", null,
                Phone(name), url, null, null, url, true, false, 80, "Good fit");
        }).ToList();

        return new LeadDiscoveryAgentResult(true, null, candidates, new LeadDiscoveryEvidence(seen, pages),
            LeadDiscoveryUsage.None, "Simulated");
    }

    /// <summary>Records what the discovery run asked it to do; sends nothing.</summary>
    private sealed class FakeConsent : IConsentInvitationService
    {
        public List<(Guid CampaignId, Guid? ExecutionId, Guid[] CustomerIds)> Sent { get; } = new();
        public int RetryCalls { get; private set; }
        public bool ThrowOnSend { get; set; }

        public Task<ConsentInvitationBatchResult> SendAsync(
            Guid campaignId, Guid? executionId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend)
                throw new InvalidOperationException("SMS provider is down");
            Sent.Add((campaignId, executionId, customerIds.ToArray()));
            return Task.FromResult(new ConsentInvitationBatchResult(customerIds.Count, 0, 0, Array.Empty<string>()));
        }

        public Task<int> RetryDueAsync(CancellationToken cancellationToken = default)
        {
            RetryCalls++;
            return Task.FromResult(0);
        }

        public Task<bool> ApplyReceiptAsync(string providerMessageId, ConsentInvitationStatus status, DateTime atUtc, string? reason, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<ConsentReplyOutcome> HandleInboundMessageAsync(
            Customer customer, InboundConsentMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(ConsentReplyOutcome.None);
    }

    /// <summary>Passes everyone unless told otherwise. Matches results by customer id only, like a real provider.</summary>
    private sealed class FakeVerifier : IPhoneVerificationProvider
    {
        public Func<Guid, string, PhoneVerificationItemResult?> Decide { get; set; } =
            (id, _) => new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);

        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public List<IReadOnlyList<PhoneVerificationItem>> Batches { get; } = new();

        public Task<PhoneVerificationResponse> VerifyAsync(
            Guid tenantId, IReadOnlyList<PhoneVerificationItem> items, CancellationToken cancellationToken = default)
        {
            Calls++;
            Batches.Add(items);
            if (Throw)
                throw new InvalidOperationException("provider down");

            // Reversed on purpose: an implementation that relies on response order would mismatch.
            var results = items.Reverse().Select(i => Decide(i.ReferenceId, i.PhoneE164)).OfType<PhoneVerificationItemResult>().ToList();
            return Task.FromResult(new PhoneVerificationResponse("req-1", results));
        }
    }

    [Fact]
    public async Task Customers_are_verified_in_one_batch_and_each_answer_lands_on_the_right_row()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, phone) => phone.EndsWith(Phone("B")[^4..])
            ? new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", "On the DND register")
            : new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(1, _verifier.Calls);
        Assert.Equal(2, _verifier.Batches.Single().Count);
        var rows = await _db.LeadDiscoveryExecutionCustomers.ToListAsync();
        var a = rows.Single(r => r.CustomerName.Contains("A"));
        var b = rows.Single(r => r.CustomerName.Contains("B"));
        Assert.Equal(LeadDiscoveryVerificationStatus.Pass, a.VerificationStatus);
        Assert.Equal(LeadDiscoveryCustomerStatus.Created, a.Status);
        Assert.Equal(LeadDiscoveryVerificationStatus.Fail, b.VerificationStatus);
        Assert.Equal("DND", b.VerificationReasonCode);
        Assert.NotNull(b.VerifiedAtUtc);
        Assert.Equal("req-1", b.VerificationRequestId);

        // The rejected number leaves a record on its row, but never becomes a customer.
        Assert.Equal(LeadDiscoveryCustomerStatus.Skipped, b.Status);
        Assert.Null(b.CustomerId);
        Assert.Contains("Rejected by the DND check", b.ErrorMessage);
        Assert.Equal(1, await _db.Customers.CountAsync());

        var events = await _db.CustomerHistoryEvents.Where(e => e.CustomerId == a.CustomerId).Select(e => e.EventType).ToListAsync();
        Assert.Contains(CustomerHistoryEventTypes.VerificationSubmitted, events);
        Assert.Contains(CustomerHistoryEventTypes.VerificationPassed, events);
    }

    [Fact]
    public async Task With_the_dnd_check_off_nothing_is_verified_and_every_lead_becomes_a_customer_in_the_campaign()
    {
        EnableAutoCampaign();
        var profile = await _db.LeadDiscoveryProfiles.SingleAsync();
        profile.DndCheckEnabled = false;
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, _) => new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", null);

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(0, _verifier.Calls);
        Assert.Equal(2, await _db.Customers.CountAsync());
        Assert.All(await _db.LeadDiscoveryExecutionCustomers.ToListAsync(),
            r => Assert.Equal(LeadDiscoveryVerificationStatus.NotChecked, r.VerificationStatus));
        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal(2, await _db.CampaignCustomers.CountAsync(cc => cc.CampaignId == generated.Id));
    }

    [Fact]
    public async Task The_dnd_check_is_off_for_a_new_profile()
    {
        Assert.False(new LeadDiscoveryProfile().DndCheckEnabled);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_rejected_number_is_kept_as_a_discovered_lead_so_it_is_not_found_again()
    {
        _agent.Rounds.Add(Candidates("A"));
        _verifier.Decide = (id, _) => new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", null);

        await Service().RunForTenantAsync(_tenant);

        var lead = await _db.DiscoveredLeads.SingleAsync();
        Assert.Null(lead.CustomerId);
        Assert.Equal(0, await _db.Customers.CountAsync());
    }

    [Fact]
    public async Task Only_customers_who_passed_verification_join_the_campaign()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, phone) => phone.EndsWith(Phone("B")[^4..])
            ? new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", "On the DND register")
            : new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Pass, null, null);

        await Service().RunForTenantAsync(_tenant);

        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        var members = await _db.CampaignCustomers.Where(cc => cc.CampaignId == generated.Id).Select(cc => cc.CustomerId).ToListAsync();
        var passed = await _db.Customers.SingleAsync(c => c.FirstName == "A Contact");
        Assert.Equal(new[] { passed.Id }, members);
        Assert.Equal(1, await _db.Customers.CountAsync());   // no customer is created for the rejected number
        Assert.Equal(1, (await SingleExecutionAsync()).MappingsEligible);
    }

    [Fact]
    public async Task No_campaign_is_created_when_no_number_passes()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A", "B"));
        _verifier.Decide = (id, _) => new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", "On the DND register");

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(1, await _db.Campaigns.CountAsync());   // only the referred one
        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryCampaignStatus.Skipped, execution.CampaignStatus);
        Assert.Contains("None of the 2 discovered numbers passed the DND check", execution.CampaignNote);
        Assert.Equal(0, await _db.Customers.CountAsync());
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
    }

    [Fact]
    public async Task A_number_that_could_not_be_verified_stays_out_until_a_retry_passes_it()
    {
        EnableAutoCampaign();
        _agent.Rounds.Add(Candidates("A"));
        _verifier.Throw = true;
        await Service().RunForTenantAsync(_tenant);

        var first = await SingleExecutionAsync();
        Assert.Equal(1, await _db.Campaigns.CountAsync());
        Assert.Equal(LeadDiscoveryExecutionStatus.RetryPending, first.Status);

        _verifier.Throw = false;
        await Service().RetryExecutionAsync(_tenant, first.Id);

        var generated = await _db.Campaigns.SingleAsync(c => c.Id != _referredCampaignId);
        Assert.Equal(1, await _db.CampaignCustomers.CountAsync(cc => cc.CampaignId == generated.Id));
        Assert.Equal(1, await _db.Customers.CountAsync());
    }

    [Fact]
    public async Task A_provider_failure_is_an_error_not_a_pass_and_does_not_fail_the_run()
    {
        _agent.Rounds.Add(Candidates("A"));
        _verifier.Throw = true;

        await Service().RunForTenantAsync(_tenant);

        // Not verified, so no customer yet: the row waits, with its candidate, for a retry.
        var row = await _db.LeadDiscoveryExecutionCustomers.SingleAsync(r => r.VerificationStatus != LeadDiscoveryVerificationStatus.NotChecked);
        Assert.Equal(LeadDiscoveryVerificationStatus.Error, row.VerificationStatus);
        Assert.Equal("PROVIDER_ERROR", row.VerificationReasonCode);
        Assert.Equal(LeadDiscoveryCustomerStatus.Pending, row.Status);
        Assert.NotNull(row.CandidateJson);
        Assert.Equal(0, await _db.Customers.CountAsync());
    }

    [Fact]
    public async Task A_number_with_no_answer_is_an_error()
    {
        _agent.Rounds.Add(Candidates("A"));
        _verifier.Decide = (_, _) => null;

        await Service().RunForTenantAsync(_tenant);

        var row = await _db.LeadDiscoveryExecutionCustomers.SingleAsync(r => r.VerificationStatus != LeadDiscoveryVerificationStatus.NotChecked);
        Assert.Equal(LeadDiscoveryVerificationStatus.Error, row.VerificationStatus);
        Assert.Equal("NO_RESULT", row.VerificationReasonCode);
        Assert.Equal(0, await _db.Customers.CountAsync());
    }

    [Fact]
    public async Task Rows_that_are_not_customers_are_never_submitted()
    {
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);

        var submitted = _verifier.Batches.Single().Select(i => i.ReferenceId).ToHashSet();
        var skipped = await _db.LeadDiscoveryExecutionCustomers.Where(r => r.Status != LeadDiscoveryCustomerStatus.Created).ToListAsync();
        Assert.All(skipped, r => Assert.Equal(LeadDiscoveryVerificationStatus.NotChecked, r.VerificationStatus));
        Assert.Single(submitted);
    }

    private sealed class FakeAgent : ILeadDiscoveryAgent
    {
        public List<LeadDiscoveryAgentResult> Rounds { get; } = new();
        public int Calls { get; private set; }

        public Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Rounds.Count == 0)
                return Task.FromResult(new LeadDiscoveryAgentResult(true, null, Array.Empty<DiscoveredBusinessCandidate>(),
                    LeadDiscoveryEvidence.Empty, LeadDiscoveryUsage.None, "Simulated"));

            var next = Rounds[0];
            Rounds.RemoveAt(0);
            return Task.FromResult(next);
        }
    }

    private sealed class SaveFailure
    {
        private readonly Func<SqliteApplicationDbContext, bool> _when;

        public SaveFailure(Func<SqliteApplicationDbContext, bool> when) => _when = when;

        public bool Enabled { get; set; } = true;

        public void Apply(SqliteApplicationDbContext context)
        {
            if (Enabled && _when(context))
                throw new InvalidOperationException("Simulated failure");
        }
    }

    /// <summary>Wraps the real store, reporting the lock lost on a chosen renewal.</summary>
    private sealed class ScriptedLockStore : ILeadDiscoveryLockStore
    {
        private readonly ILeadDiscoveryLockStore _inner;
        private int _renewals;

        public ScriptedLockStore(ILeadDiscoveryLockStore inner) => _inner = inner;

        public int? LoseOnRenewal { get; set; }

        public Task<LeadDiscoveryLockAcquireResult> TryAcquireAsync(LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default) =>
            _inner.TryAcquireAsync(claim, lease, cancellationToken);

        public Task<LeadDiscoveryLockOperationResult> RenewAsync(LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default) =>
            ++_renewals == LoseOnRenewal
                ? Task.FromResult(new LeadDiscoveryLockOperationResult(LeadDiscoveryLockOwnership.Lost, null))
                : _inner.RenewAsync(claim, lease, cancellationToken);

        public Task<LeadDiscoveryLockOperationResult> ReleaseAsync(LeadDiscoveryLockClaim claim, CancellationToken cancellationToken = default) =>
            _inner.ReleaseAsync(claim, cancellationToken);

        public Task RecordTransitionAsync(LeadDiscoveryLockTransitionRecord record, CancellationToken cancellationToken = default) =>
            _inner.RecordTransitionAsync(record, cancellationToken);
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public TestTenantContext(Guid? tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class IstTimeZone : ITenantTimeZoneProvider
    {
        private readonly IDateTimeProvider _clock;
        public IstTimeZone(IDateTimeProvider clock) => _clock = clock;
        public Task<DateTime> GetLocalNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(_clock.IstNow);
    }

    private sealed class Snapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public Snapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}

/// <summary>A no-op implementation of any interface, with individual methods overridden by name. Unhandled
/// methods return a completed Task, a Task of default, or default.</summary>
public class Stub<T> : DispatchProxy where T : class
{
    private Dictionary<string, Func<object?[]?, object?>> _handlers = new();

    public static T Create(Dictionary<string, Func<object?[]?, object?>> handlers)
    {
        var proxy = Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy)._handlers = handlers;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (_handlers.TryGetValue(targetMethod.Name, out var handler))
            return handler(args);

        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, new[] { value });
        }

        return returnType.IsValueType && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
    }
}
