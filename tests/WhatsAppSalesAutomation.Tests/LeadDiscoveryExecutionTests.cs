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
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
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

    private int _quotaChecks;
    private decimal _quotaConsumed;
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
    public async Task A_used_up_monthly_limit_is_recorded_as_that_with_the_reason_and_is_not_reported_as_out_of_credits()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        var notified = false;
        var quotaAlerts = Stub<IQuotaAlertService>.Create(new()
        {
            ["NotifyIfExhaustedAsync"] = _ =>
            {
                notified = true;
                return Task.FromResult(true);
            }
        });
        var limit = new QuotaShortfall(QuotaShortfallKind.MonthlyLimitReached, "Lead discovery has used its monthly limit of 25 credits. Your wallet still has 813 credits.");

        await Service(quotaAvailable: 0, quotaAlerts: quotaAlerts, shortfall: limit).RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.False(execution.QuotaExhausted); // not "out of credits"
        Assert.Equal(limit.Message, execution.QuotaStopReason);
        Assert.Contains("monthly limit of 25 credits", execution.ErrorMessage);
        Assert.Equal(0, _agent.Calls);
        Assert.False(notified); // no "you are out of credits" notice to a tenant whose wallet is full
    }

    [Fact]
    public async Task Running_out_of_credits_still_says_so_and_keeps_its_reason()
    {
        _agent.Rounds.Add(Candidates("A", "B"));
        var out_ = new QuotaShortfall(QuotaShortfallKind.InsufficientCredits, "Your wallet has 0 credits, which is not enough for Lead discovery. Buy credits to continue.");

        await Service(quotaAvailable: 0, shortfall: out_).RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.True(execution.QuotaExhausted);
        Assert.Equal(out_.Message, execution.QuotaStopReason);
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
    public async Task With_the_consent_sms_off_each_customer_the_campaign_took_gets_one_history_entry_saying_why_no_sms_was_sent()
    {
        await RunWithOneRejectedAsync();   // A passes and joins the campaign, B is rejected by the DND check; the consent SMS is off

        var passed = await _db.Customers.SingleAsync(c => c.FirstName == "A Contact");
        var skipped = await _db.CustomerHistoryEvents.Where(e => e.EventType == CustomerHistoryEventTypes.SmsSkipped).ToListAsync();

        var entry = Assert.Single(skipped);                  // the rejected number has no customer, so nothing is recorded for it
        Assert.Equal(passed.Id, entry.CustomerId);
        Assert.Equal(LeadDiscoveryRunService.ConsentSmsOffReason, entry.Reason);
        Assert.Equal((await SingleExecutionAsync()).Id, entry.ExecutionId);
        Assert.Empty(_consent.Sent);
    }

    [Fact]
    public async Task The_consent_sms_column_is_never_blank_it_says_why_nothing_was_sent()
    {
        await RunWithOneRejectedAsync();
        var execution = await SingleExecutionAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));

        var detail = await history.GetExecutionAsync(execution.Id);

        Assert.All(detail.Customers, c => Assert.False(string.IsNullOrWhiteSpace(c.ConsentSmsNote), c.CustomerName));
        var rejected = detail.Customers.Single(c => c.CustomerName.Contains("B"));
        Assert.Equal("Not sent: Not applicable", rejected.ConsentSmsNote);   // the SMS logic is skipped for a number that failed the DND check
        var passed = detail.Customers.Single(c => c.CustomerName.Contains("A"));
        Assert.Equal("Not sent: the consent SMS was Off", passed.ConsentSmsNote);
        Assert.True(passed.ConsentSmsNeedsSetting);      // the screen links this one to the discovery profile
        Assert.False(rejected.ConsentSmsNeedsSetting);   // a rejected number is not fixed by a setting
        Assert.All(detail.Customers.Where(c => c.CustomerId is null), c => Assert.StartsWith("Not sent:", c.ConsentSmsNote));

    }

    [Fact]
    public void A_business_with_no_customer_that_was_not_rejected_is_not_applicable_and_a_customer_with_a_specific_skip_reason_keeps_it()
    {
        LeadDiscoveryExecutionCustomerDto Row(Guid? customerId, string verification) =>
            new(Guid.NewGuid(), customerId, null, "Closed Business", null, "Skipped", true, null, null, null, null, verification);

        var none = new Dictionary<Guid, string?>();
        Assert.Equal(("Not sent: Not applicable", false), LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(null, "NotChecked"), none));

        var customerId = Guid.NewGuid();
        var reasons = new Dictionary<Guid, string?> { [customerId] = "The business's WhatsApp number is not set up, so the invitation has nowhere to point." };
        var (note, needsSetting) = LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(customerId, "Pass"), reasons);
        Assert.StartsWith("Not sent: The business's WhatsApp number is not set up", note);
        Assert.False(needsSetting);

        // The recorded "switch is Off" reason and a missing record both read the same, and link to the setting.
        reasons[customerId] = LeadDiscoveryRunService.ConsentSmsOffReason;
        Assert.Equal(("Not sent: the consent SMS was Off", true), LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(customerId, "Pass"), reasons));
        Assert.Equal(("Not sent: the consent SMS was Off", true), LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(customerId, "Pass"), none));
    }

    // ---- consent SMS on, but no Auto-Campaign: nothing can be sent, and the screen must say that - not "Off" ----

    private LeadDiscoveryHistoryService History2() =>
        new(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()));

    private void TurnConsentSmsOn()
    {
        _db.LeadDiscoveryProfiles.Single().ConsentSmsEnabled = true;
        _db.SaveChanges();
        // A published consent page, so the run is allowed to start (see ConsentPageBlockAsync).
        PublishConsentPage();
    }

    [Fact]
    public async Task With_the_consent_sms_on_but_no_auto_campaign_each_customer_is_told_why_no_sms_was_sent()
    {
        TurnConsentSmsOn();
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        Assert.False(execution.AutoCampaignConfigured);
        Assert.Empty(_consent.Sent);                                                       // no campaign took them, so nobody was texted
        var recorded = await _db.CustomerHistoryEvents.Where(e => e.EventType == CustomerHistoryEventTypes.SmsSkipped).ToListAsync();
        Assert.Equal(2, recorded.Count);                                                   // one per customer
        Assert.All(recorded, e => Assert.Equal(LeadDiscoveryRunService.AutoCampaignNotConfiguredReason, e.Reason));
        Assert.All(recorded, e => Assert.Equal(execution.Id, e.ExecutionId));

        var detail = await History2().GetExecutionAsync(execution.Id);
        Assert.All(detail.Customers, c =>
        {
            Assert.StartsWith("Not sent: Auto-Campaign is not configured", c.ConsentSmsNote);
            Assert.DoesNotContain("was Off", c.ConsentSmsNote);                            // the switch is on - it must not say it was off
            Assert.True(c.ConsentSmsNeedsSetting);                                         // the screen links to the discovery profile
        });
    }

    [Fact]
    public async Task An_execution_from_before_the_reason_was_recorded_still_explains_itself_from_the_execution()
    {
        TurnConsentSmsOn();
        _agent.Rounds.Add(Candidates("A"));
        await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);
        // As written by an earlier version: no reason on the customer's history.
        _db.CustomerHistoryEvents.RemoveRange(_db.CustomerHistoryEvents.Where(e => e.EventType == CustomerHistoryEventTypes.SmsSkipped));
        await _db.SaveChangesAsync();

        var detail = await History2().GetExecutionAsync((await SingleExecutionAsync()).Id);

        Assert.StartsWith("Not sent: Auto-Campaign is not configured", Assert.Single(detail.Customers).ConsentSmsNote);
    }

    [Fact]
    public async Task With_the_consent_sms_off_and_no_auto_campaign_it_is_still_the_switch_that_is_named()
    {
        _agent.Rounds.Add(Candidates("A"));                                                // ConsentSmsEnabled is off in the default profile

        await Service().RunForTenantAsync(_tenant);

        Assert.Empty(await _db.CustomerHistoryEvents.Where(e => e.EventType == CustomerHistoryEventTypes.SmsSkipped).ToListAsync());
        var detail = await History2().GetExecutionAsync((await SingleExecutionAsync()).Id);
        var row = Assert.Single(detail.Customers);
        Assert.Equal("Not sent: the consent SMS was Off", row.ConsentSmsNote);
        Assert.True(row.ConsentSmsNeedsSetting);
    }

    [Fact]
    public void The_note_names_the_missing_auto_campaign_only_when_the_switch_is_on_and_a_recorded_reason_always_wins()
    {
        LeadDiscoveryExecutionCustomerDto Row(Guid customerId) =>
            new(Guid.NewGuid(), customerId, null, "A", null, "Created", false, null, null, null, null, "NotChecked");
        var id = Guid.NewGuid();
        var none = new Dictionary<Guid, string?>();

        // switch on, no campaign -> the missing campaign
        Assert.Equal(("Not sent: " + LeadDiscoveryRunService.AutoCampaignNotConfiguredReason, true),
            LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(id), none, autoCampaignConfigured: false, consentSmsEnabled: true));
        // switch off, no campaign -> the switch
        Assert.Equal(("Not sent: the consent SMS was Off", true),
            LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(id), none, autoCampaignConfigured: false, consentSmsEnabled: false));
        // a campaign was there and the switch is off -> the switch, as before
        Assert.Equal(("Not sent: the consent SMS was Off", true),
            LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(id), none, autoCampaignConfigured: true, consentSmsEnabled: false));
        // a specific reason recorded by the run wins over either
        var reasons = new Dictionary<Guid, string?> { [id] = "The SMS provider turned the number away." };
        Assert.Equal(("Not sent: The SMS provider turned the number away.", false),
            LeadDiscoveryHistoryService.ConsentSmsNoteFor(Row(id), reasons, autoCampaignConfigured: false, consentSmsEnabled: true));
    }

    [Fact]
    public async Task A_row_that_was_sent_an_sms_has_its_status_and_no_note()
    {
        await RunWithOneRejectedAsync();
        var execution = await SingleExecutionAsync();
        var passed = await _db.Customers.SingleAsync(c => c.FirstName == "A Contact");
        _db.ConsentInvitations.Add(new ConsentInvitation
        {
            TenantId = _tenant, CustomerId = passed.Id, CampaignId = execution.GeneratedCampaignId, ExecutionId = execution.Id,
            IdempotencyKey = "k-a", ToPhoneE164 = passed.PhoneNumberE164, Body = "b", WhatsAppLink = "https://wa.me/1",
            Status = ConsentInvitationStatus.Delivered
        });
        await _db.SaveChangesAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant),
            Stub<ILeadDiscoveryRetryScheduler>.Create(new()));

        var detail = await history.GetExecutionAsync(execution.Id);

        var row = detail.Customers.Single(c => c.CustomerName.Contains("A"));
        Assert.Equal("Delivered", row.ConsentSmsStatus);
        Assert.Null(row.ConsentSmsNote);
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
        Assert.Equal("Lead only", detail.Customers.Single(c => c.CustomerName.Contains("B")).Outcome);          // kept as a lead, counted as saved
        Assert.Equal("Saved", detail.Customers.Single(c => c.CustomerName.Contains("A")).Outcome);
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

    private LeadDiscoveryRunService Service(
        ILeadDiscoveryLockStore? lockStore = null, decimal quotaAvailable = 1000m, IQuotaAlertService? quotaAlerts = null, IConsentPageGate? consentPageGate = null, QuotaShortfall? shortfall = null)
    {
        var planLimits = Stub<IPlanLimitsService>.Create(new()
        {
            ["GetLeadDiscoveryBatchLimitAsync"] = _ => Task.FromResult<int?>(null),
            ["EnsureCanCreateCampaignAsync"] = _ => _beforeCampaignCreate?.Invoke() ?? Task.CompletedTask
        });
        var quota = Stub<IQuotaGate>.Create(new()
        {
            ["GetAvailableAsync"] = _ =>
            {
                _quotaChecks++;
                return Task.FromResult(quotaAvailable);
            },
            ["GetShortfallAsync"] = _ => Task.FromResult(shortfall),
            ["ConsumeLeadCandidatesAsync"] = args =>
            {
                _quotaConsumed += (decimal)args![1]!;
                return Task.FromResult((decimal)args![1]!);
            }
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
            new Snapshot<LeadDiscoveryOptions>(_discoveryOptions), new Snapshot<LeadDiscoveryPricingOptions>(new LeadDiscoveryPricingOptions()), _verifier, _consent,
            consentPageGate);
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

    // ---------------------------------------------------------------------------------------------
    // WhatsApp Consent Page: validated before anything is searched or charged
    // ---------------------------------------------------------------------------------------------

    private RecordingNotifier _notices = new();

    private IConsentPageGate RealGate() => new ConsentPageGate(
        _db, _notices, Options.Create(new ConsentPageOptions { PublicBaseUrl = "https://app.example.com" }), NullLogger<ConsentPageGate>.Instance);

    private void RequireConsentPage() => _db.LeadDiscoveryProfiles.Single().ConsentSmsEnabled = true;

    private void PublishConsentPage(bool active = true)
    {
        _db.TenantConsentPages.Add(new TenantConsentPage
        {
            TenantId = _tenant, BusinessName = "Acme", ProductDescription = "Eye tests and glasses.", IsActive = active,
            PublishedVersion = 1, PublishedAtUtc = _clock.UtcNow
        });
        _db.ConsentPageVersions.Add(new ConsentPageVersion
        {
            TenantId = _tenant, Version = 1, BusinessName = "Acme", ProductDescription = "Eye tests and glasses.",
            ConsentStatement = ConsentPageRules.ResolveStatement(null, "Acme", null), PublishedAtUtc = _clock.UtcNow
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Without_a_consent_page_discovery_does_not_search_charge_or_start_and_the_tenant_is_told()
    {
        RequireConsentPage();
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A", "B"));

        var summary = await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        Assert.StartsWith("Skipped:", summary);
        Assert.Contains("No discovery credits were deducted", summary);
        Assert.Equal(0, _agent.Calls);                      // the discovery provider was never called
        Assert.Equal(0, _quotaChecks);                      // the wallet was not even consulted
        Assert.Equal(0m, _quotaConsumed);                   // and nothing was reserved or deducted
        Assert.Empty(await _db.Customers.ToListAsync());
        Assert.Empty(await _db.LeadDiscoveryRuns.ToListAsync());
        Assert.Empty(_startedCampaigns);

        // The attempt is recorded as a skipped run - not a started, paid one - so the tenant can see why nothing happened.
        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.Contains("consent", execution.Summary, StringComparison.OrdinalIgnoreCase);

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal(_tenant, notice.TenantId);
        Assert.Equal(TenantNotificationKind.ConsentPageSetupRequired, notice.Kind);
        Assert.Equal("WhatsApp Consent Page Setup Required", notice.Title);
        Assert.Equal(
            "Your WhatsApp Consent Page configuration is incomplete or unpublished. Please complete and publish your product information and WhatsApp consent statement before starting customer discovery. No discovery credits have been deducted.",
            notice.Body);
    }

    [Fact]
    public async Task The_same_unresolved_problem_does_not_notify_again_but_fixing_and_breaking_it_again_does()
    {
        RequireConsentPage();
        await _db.SaveChangesAsync();
        var service = Service(consentPageGate: RealGate());

        await service.RunForTenantAsync(_tenant);
        await service.RunForTenantAsync(_tenant);
        await service.RunForTenantAsync(_tenant);
        Assert.Single(_notices.Sent);

        // Published, then switched off: a new situation, a new notice.
        PublishConsentPage(active: false);
        await service.RunForTenantAsync(_tenant);
        Assert.Equal(2, _notices.Sent.Count);
    }

    [Fact]
    public async Task A_draft_that_was_never_published_does_not_qualify()
    {
        RequireConsentPage();
        _db.TenantConsentPages.Add(new TenantConsentPage { TenantId = _tenant, BusinessName = "Acme", ProductDescription = "Eye tests and glasses." });
        await _db.SaveChangesAsync();

        var summary = await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        Assert.StartsWith("Skipped:", summary);
        Assert.Equal(0, _agent.Calls);
        Assert.Single(_notices.Sent);
    }

    [Fact]
    public async Task A_published_active_page_lets_discovery_run_under_the_normal_billing_rules()
    {
        RequireConsentPage();
        PublishConsentPage();
        _agent.Rounds.Add(Candidates("A", "B"));

        var summary = await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        Assert.DoesNotContain("consent page", summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(_agent.Calls >= 1);
        Assert.True(_quotaConsumed > 0m);
        Assert.Equal(2, await _db.Customers.CountAsync());
        Assert.Empty(_notices.Sent);
    }

    [Fact]
    public async Task A_page_that_is_switched_off_blocks_discovery()
    {
        RequireConsentPage();
        PublishConsentPage(active: false);
        _agent.Rounds.Add(Candidates("A"));

        var summary = await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        Assert.StartsWith("Skipped:", summary);
        Assert.Equal(0, _agent.Calls);
        Assert.Equal(0m, _quotaConsumed);
    }

    [Fact]
    public async Task Discovery_that_sends_no_consent_sms_does_not_need_the_page()
    {
        _agent.Rounds.Add(Candidates("A"));   // ConsentSmsEnabled is off in the default profile

        await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        Assert.True(_agent.Calls >= 1);
        Assert.Empty(_notices.Sent);
    }

    [Fact]
    public async Task Fixing_the_page_does_not_start_a_discovery_by_itself()
    {
        RequireConsentPage();
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates("A"));
        await Service(consentPageGate: RealGate()).RunForTenantAsync(_tenant);

        PublishConsentPage();   // the tenant completes the setup

        // Nothing runs until a run is started - by the tenant, or by the schedule that already existed.
        Assert.Equal(0, _agent.Calls);
        Assert.Empty(await _db.Customers.ToListAsync());
    }

    [Fact]
    public async Task A_retry_of_an_earlier_execution_waits_for_the_page_too_and_stays_retryable()
    {
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);
        var first = await SingleExecutionAsync();
        var callsBefore = _agent.Calls;
        var consumedBefore = _quotaConsumed;

        RequireConsentPage();
        await _db.SaveChangesAsync();
        var summary = await Service(consentPageGate: RealGate()).RetryExecutionAsync(_tenant, first.Id);

        Assert.StartsWith("Skipped:", summary);
        Assert.Equal(callsBefore, _agent.Calls);
        Assert.Equal(consumedBefore, _quotaConsumed);
        Assert.Single(await Executions());   // no new execution was started
    }


    // ---------------------------------------------------------------------------------------------
    // Billing and the size of the batch
    // ---------------------------------------------------------------------------------------------

    private void SetBatchSize(int batchSize)
    {
        _db.LeadDiscoveryProfiles.Single().BatchSize = batchSize;
        _db.SaveChanges();
    }

    /// <summary>Candidates the qualification rules always reject: closed businesses.</summary>
    private static LeadDiscoveryAgentResult Rejected(params string[] names)
    {
        var good = Candidates(names);
        var closed = good.Candidates.Select(c => c with { IsPermanentlyClosed = true }).ToList();
        return good with { Candidates = closed };
    }

    private static LeadDiscoveryAgentResult Together(params LeadDiscoveryAgentResult[] parts) =>
        new(true, null, parts.SelectMany(p => p.Candidates).ToList(),
            new LeadDiscoveryEvidence(parts.SelectMany(p => p.Evidence.SeenUrls).ToList(), parts.SelectMany(p => p.Evidence.FetchedPages).ToList()),
            LeadDiscoveryUsage.None, "Simulated");

    [Fact]
    public async Task The_tenant_is_charged_only_for_the_customers_created_never_for_rejected_candidates()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Together(Candidates("A", "B"), Rejected("C", "D", "E", "F")));   // 6 found, 2 valid

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(2, await _db.Customers.CountAsync());
        Assert.Equal(2m, _quotaConsumed);                                                  // two leads, not six candidates
        var rejected = await _db.LeadDiscoveryExecutionCustomers.Where(r => r.IsInvalid).ToListAsync();
        Assert.Equal(4, rejected.Count);
        Assert.All(rejected, r => Assert.Contains("(not charged)", r.ErrorMessage));
    }

    [Fact]
    public async Task Every_candidate_considered_is_on_the_execution_with_what_became_of_it()
    {
        SetBatchSize(2);
        // 7 found: 4 qualify (2 are needed), 2 are rejected, 1 already exists.
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("G"), FirstName = "Existing", Source = "Manual" });
        _db.SaveChanges();
        _agent.Rounds.Add(Together(Candidates("A", "B", "C", "D"), Rejected("E", "F"), Candidates("G")));

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var history = new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()));
        var detail = await history.GetExecutionAsync(execution.Id);

        Assert.Equal(7, detail.Customers.Count);                                           // as many rows as candidates considered
        Assert.Equal(7, detail.Research!.CandidatesConsidered);
        Assert.Equal(2, detail.Customers.Count(c => c.Outcome == "Saved"));
        Assert.Equal(1, detail.Customers.Count(c => c.Outcome == "Duplicate"));
        Assert.Equal(2, detail.Customers.Count(c => c.Outcome == "Rejected"));
        var notNeeded = detail.Customers.Where(c => c.Outcome == "Not needed").ToList();
        Assert.Equal(2, notNeeded.Count);
        Assert.All(notNeeded, c =>
        {
            Assert.Equal("Skipped", c.Status);
            Assert.False(c.IsInvalid);
            Assert.Null(c.CustomerId);
            Assert.Contains("batch was already full", c.ErrorMessage);
        });
        Assert.Equal(2, await _db.Customers.CountAsync(c => c.Source != "Manual"));        // the not-needed ones were not saved
        Assert.Equal(2m, _quotaConsumed);                                                  // nor charged
    }

    [Fact]
    public async Task Every_row_keeps_what_the_agent_reported_about_its_candidate_whatever_became_of_it()
    {
        SetBatchSize(2);
        _db.Customers.Add(new Customer { TenantId = _tenant, PhoneNumberE164 = Phone("G"), FirstName = "Existing", Source = "Manual" });
        _db.SaveChanges();
        _agent.Rounds.Add(Together(Candidates("A", "B", "C"), Rejected("D"), Candidates("G")));   // saved x2, not needed, rejected, duplicate

        await Service().RunForTenantAsync(_tenant);

        var execution = await SingleExecutionAsync();
        var detail = await new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()))
            .GetExecutionAsync(execution.Id);

        Assert.Equal(new[] { "Duplicate", "Not needed", "Rejected", "Saved", "Saved" }, detail.Customers.Select(c => c.Outcome).Order().ToArray());
        Assert.All(detail.Customers, c => Assert.NotNull(c.Candidate));

        var saved = detail.Customers.Single(c => c.CustomerName == "A");
        Assert.Equal("Eye clinic", saved.Candidate!.BusinessType);
        Assert.Equal("A Contact", saved.Candidate.ContactPerson);
        Assert.Equal("Chandigarh", saved.Candidate.City);
        Assert.Equal(80, saved.Candidate.LeadScore);
        Assert.Equal("Good fit", saved.Candidate.ScoreRationale);
        Assert.Contains("a-clinic", saved.Candidate.SourceUrl);

        // A candidate that was never saved still has its details - the reason it was left out is on the row, the facts are here.
        var rejected = detail.Customers.Single(c => c.Outcome == "Rejected");
        Assert.Equal("D", rejected.CustomerName);
        Assert.True(rejected.Candidate!.IsPermanentlyClosed);
        Assert.Equal("Chandigarh", rejected.Candidate.City);
        Assert.Equal("Good fit", rejected.Candidate.ScoreRationale);
        Assert.NotNull(detail.Customers.Single(c => c.Outcome == "Not needed").Candidate);
        Assert.NotNull(detail.Customers.Single(c => c.Outcome == "Duplicate").Candidate);
    }

    // ---------------------------------------------------------------------------------------------
    // Justifying what a run cost and found
    // ---------------------------------------------------------------------------------------------

    private LeadDiscoveryHistoryService History() =>
        new(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()));

    private static LeadDiscoveryAgentResult With(LeadDiscoveryAgentResult result, string? note, LeadDiscoveryUsage? usage = null) =>
        result with { Note = note, Usage = usage ?? result.Usage, Model = "gpt-5-mini" };

    [Fact]
    public async Task A_run_that_found_nothing_keeps_the_agents_reason_and_shows_it_on_the_execution()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(With(Candidates(), "No clinic page named a contact person, which the profile requires."));
        _agent.Rounds.Add(With(Candidates(), "Searched chains only; none showed an address."));

        await Service().RunForTenantAsync(_tenant);

        var detail = await History().GetExecutionAsync((await SingleExecutionAsync()).Id);
        Assert.Equal(0, detail.Research!.CandidatesConsidered);
        Assert.Equal(
            "Round 1: No clinic page named a contact person, which the profile requires.\nRound 2: Searched chains only; none showed an address.",
            detail.Research.ResearchNote);
    }

    [Fact]
    public async Task A_single_round_keeps_the_agents_words_as_they_are()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(With(Candidates("A", "B"), "Searched dental clinics in Chandigarh."));

        await Service().RunForTenantAsync(_tenant);

        var run = await _db.LeadDiscoveryRuns.SingleAsync();
        Assert.Equal("Searched dental clinics in Chandigarh.", run.ResearchNote);
    }

    [Fact]
    public async Task A_run_with_no_explanation_from_the_agent_has_no_note()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Null((await _db.LeadDiscoveryRuns.SingleAsync()).ResearchNote);
    }

    [Fact]
    public async Task The_cost_of_a_run_is_recorded_as_tokens_plus_searches_and_the_parts_add_up_to_the_total()
    {
        SetBatchSize(2);
        var usage = new LeadDiscoveryUsage(78_603, 2_895, 46_208, 0, WebSearches: 7, WebFetches: 2);
        _agent.Rounds.Add(With(Candidates("A", "B"), "Searched.", usage));

        await Service().RunForTenantAsync(_tenant);

        var run = await _db.LeadDiscoveryRuns.SingleAsync();
        Assert.Equal("gpt-5-mini", run.Model);
        Assert.Equal(0.07m, run.SearchCostUsd);                                  // 7 searches x $10 / 1000
        Assert.Equal(0.096596m, run.EstimatedCostUsd);                            // gpt-5-mini's own rates, not the default Claude model's
        var detail = await History().GetExecutionAsync(run.ExecutionId!.Value);
        Assert.NotNull(detail.Research!.SearchCostLocal);
        Assert.NotNull(detail.Research.TokensCostLocal);
        Assert.InRange(detail.Research.SearchCostLocal!.Value + detail.Research.TokensCostLocal!.Value - detail.Research.EstimatedCostLocal, -0.011m, 0.011m);
    }

    [Fact]
    public async Task A_run_recorded_before_the_split_existed_shows_no_split_but_still_its_total()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));
        await Service().RunForTenantAsync(_tenant);
        var run = await _db.LeadDiscoveryRuns.SingleAsync();
        run.SearchCostUsd = null;
        run.ResearchNote = null;
        await _db.SaveChangesAsync();

        var detail = await History().GetExecutionAsync(run.ExecutionId!.Value);

        Assert.Null(detail.Research!.SearchCostLocal);
        Assert.Null(detail.Research.TokensCostLocal);
        Assert.Null(detail.Research.ResearchNote);
    }

    [Fact]
    public async Task The_execution_shows_the_credits_it_actually_took_from_the_wallet_net_of_a_refund()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));
        await Service().RunForTenantAsync(_tenant);
        var execution = await SingleExecutionAsync();
        var key = execution.Id.ToString("N");
        QuotaLedgerEntry Entry(decimal units, string reference) => new()
        {
            TenantId = _tenant, QuotaType = QuotaType.Credits, EntryType = QuotaEntryType.Consumption, UnitsDelta = units,
            OperationKey = Guid.NewGuid().ToString("N"), ReferenceType = "LeadDiscoveryRun", ReferenceId = reference
        };
        _db.QuotaLedgerEntries.AddRange(
            Entry(-1.5m, key), Entry(-1.5m, key),     // two leads charged
            Entry(1.5m, key),                         // one of them refunded
            Entry(-9m, Guid.NewGuid().ToString("N"))); // another execution's charge
        await _db.SaveChangesAsync();

        var detail = await History().GetExecutionAsync(execution.Id);

        Assert.Equal(1.5m, detail.CreditsCharged);
    }

    [Fact]
    public async Task An_execution_that_charged_nothing_shows_zero_credits_not_a_negative_figure()
    {
        _agent.Rounds.Add(Rejected("X"));

        await Service().RunForTenantAsync(_tenant);

        var detail = await History().GetExecutionAsync((await SingleExecutionAsync()).Id);
        Assert.Equal(0m, detail.CreditsCharged);
    }

    [Fact]
    public async Task A_row_from_before_candidates_were_kept_shows_no_details_and_still_reads()
    {
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);
        var row = await _db.LeadDiscoveryExecutionCustomers.SingleAsync();
        row.CandidateDetailsJson = null;                       // as written by an older version
        await _db.SaveChangesAsync();

        var detail = await new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()))
            .GetExecutionAsync((await SingleExecutionAsync()).Id);

        Assert.Null(Assert.Single(detail.Customers).Candidate);
        Assert.Equal("Saved", detail.Customers[0].Outcome);
    }

    [Fact]
    public async Task Damaged_details_on_a_row_do_not_make_the_execution_unreadable()
    {
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);
        var row = await _db.LeadDiscoveryExecutionCustomers.SingleAsync();
        row.CandidateDetailsJson = "{ not json";
        await _db.SaveChangesAsync();

        var detail = await new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()))
            .GetExecutionAsync((await SingleExecutionAsync()).Id);

        Assert.Null(Assert.Single(detail.Customers).Candidate);
    }

    [Fact]
    public async Task A_retry_keeps_the_details_of_the_candidates_it_carries_over()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));
        var failure = FailCustomerSave("B Contact");
        await Service().RunForTenantAsync(_tenant);
        var first = await SingleExecutionAsync();

        failure.Enabled = false;
        await Service().RetryExecutionAsync(_tenant, first.Id);

        var retry = (await Executions()).Last();
        var detail = await new LeadDiscoveryHistoryService(_db, new TestTenantContext(_tenant), Stub<ILeadDiscoveryRetryScheduler>.Create(new()))
            .GetExecutionAsync(retry.Id);

        Assert.NotEmpty(detail.Customers);
        Assert.All(detail.Customers, c => Assert.NotNull(c.Candidate));
        Assert.Equal("B Contact", detail.Customers.Single(c => c.CustomerName == "B").Candidate!.ContactPerson);
    }

    [Fact]
    public async Task A_retry_does_not_carry_the_not_needed_candidates_over_or_create_them()
    {
        SetBatchSize(1);
        _agent.Rounds.Add(Candidates("A", "B", "C"));                                      // one needed; B and C are not needed
        var failure = FailCustomerSave("A Contact");
        await Service().RunForTenantAsync(_tenant);
        var first = await SingleExecutionAsync();

        failure.Enabled = false;
        await Service().RetryExecutionAsync(_tenant, first.Id);

        var names = (await _db.Customers.ToListAsync()).Select(c => c.FirstName).ToList();
        Assert.Equal(new[] { "A Contact" }, names);                                        // only the one that was needed
    }

    [Fact]
    public async Task Each_customer_is_charged_under_its_own_key_with_the_business_named()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));
        var charges = new List<(string Key, string Note)>();

        var service = ServiceWithChargeLog(charges);
        await service.RunForTenantAsync(_tenant);

        Assert.Equal(2, charges.Count);
        Assert.Equal(2, charges.Select(c => c.Key).Distinct().Count());
        Assert.All(charges, c => Assert.StartsWith("lead:", c.Key));
        Assert.Contains(charges, c => c.Note == "Lead: A");
    }

    [Fact]
    public async Task A_duplicate_is_not_charged()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A"));
        await Service().RunForTenantAsync(_tenant);
        var afterFirst = _quotaConsumed;

        _agent.Rounds.Add(Candidates("A"));                                                // found again - already a customer

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(1m, afterFirst);
        Assert.Equal(afterFirst, _quotaConsumed);
    }

    [Fact]
    public async Task The_run_keeps_searching_until_the_batch_is_full_even_when_a_round_finds_only_rejects()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Rejected("A", "B", "C"));                                        // nothing usable
        _agent.Rounds.Add(Together(Rejected("D"), Candidates("E")));                       // one usable
        _agent.Rounds.Add(Candidates("F"));                                                // the second

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(new[] { "E Contact", "F Contact" }, (await _db.Customers.OrderBy(c => c.FirstName).ToListAsync()).Select(c => c.FirstName));
        Assert.Equal(3, _agent.Calls);
        Assert.Equal(2m, _quotaConsumed);
        var execution = await SingleExecutionAsync();
        Assert.Equal(2, execution.CustomersCreated);
        Assert.Equal(4, execution.CustomersInvalid);
    }

    [Fact]
    public async Task Businesses_already_rejected_are_told_to_the_agent_so_the_next_round_finds_new_ones()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Rejected("A", "B"));
        _agent.Rounds.Add(Candidates("C", "D"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(2, _agent.Requests.Count);
        Assert.Empty(_agent.Requests[0].KnownBusinesses);
        Assert.Contains("A, Chandigarh", _agent.Requests[1].KnownBusinesses);
        Assert.Contains("B, Chandigarh", _agent.Requests[1].KnownBusinesses);
    }

    [Fact]
    public async Task With_no_history_the_first_round_asks_for_the_batch_plus_the_configured_starting_guess_of_waste()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(3, _agent.Requests[0].MaxCandidates);                                 // 2 / (1 - 30%) = 2.9, rounded up
    }

    // ---- how many to ask for: learned from the tenant's own runs ----

    /// <summary>An earlier run of a profile, with the execution it belongs to - the learning reads runs through their executions. By default it is
    /// an hour from now, which is after the profile was last saved (see PriorUnusableRateAsync); a test about older runs passes its own time.</summary>
    private void RecordRealRun(int considered, int rejected, int duplicates, string model = "gpt-5-mini", DateTime? at = null, Guid? profileId = null)
    {
        var ranAt = at ?? DateTime.UtcNow.AddHours(1);
        var execution = new LeadDiscoveryExecution
        {
            TenantId = _tenant, LeadDiscoveryProfileId = profileId ?? _profileId, ProfileName = "Eye clinic", ProcessingDate = ranAt.Date,
            Trigger = LeadDiscoveryTriggers.Scheduled, StartedAtUtc = ranAt, EndedAtUtc = ranAt, Status = LeadDiscoveryExecutionStatus.Completed,
            LockKey = "earlier-run", CampaignStatus = LeadDiscoveryCampaignStatus.Skipped, TemplateStatus = LeadDiscoveryAssociationStatus.Skipped,
            MappingStatus = LeadDiscoveryAssociationStatus.Skipped
        };
        execution.RootExecutionId = execution.Id;
        _db.LeadDiscoveryExecutions.Add(execution);
        _db.LeadDiscoveryRuns.Add(new LeadDiscoveryRun
        {
            TenantId = _tenant, ExecutionId = execution.Id, RanAtUtc = ranAt, Model = model, Rounds = 1,
            CandidatesConsidered = considered, LeadsSaved = considered - rejected - duplicates, Rejected = rejected, Duplicates = duplicates
        });
    }

    /// <summary>When the profile's rules last changed. Written straight to the table, so saving the profile does not stamp it again.</summary>
    private void ProfileLastChangedAt(DateTime whenUtc) =>
        _db.LeadDiscoveryProfiles.ExecuteUpdate(p => p.SetProperty(x => x.UpdatedAt, whenUtc));

    private static readonly string[] Letters = Enumerable.Range('A', 20).Select(c => ((char)c).ToString()).ToArray();

    [Fact]
    public async Task Example_A_a_tenant_whose_searches_throw_away_2_in_12_is_asked_for_12_to_fill_a_batch_of_10_not_20()
    {
        SetBatchSize(10);
        RecordRealRun(considered: 12, rejected: 2, duplicates: 0);
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(12, _agent.Requests[0].MaxCandidates);
    }

    [Fact]
    public async Task A_tenant_with_a_high_rejection_history_is_asked_for_more_up_to_the_ceiling()
    {
        SetBatchSize(10);
        RecordRealRun(considered: 20, rejected: 12, duplicates: 0);                         // 60%
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(8).ToArray()));                           // 8 get through, 2 short
        _agent.Rounds.Add(Candidates("S", "T"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(20, _agent.Requests[0].MaxCandidates);                                // 10 / 0.4 = 25, capped at 20 a round
        Assert.True(_agent.Requests[1].MaxCandidates < 8, $"asked for {_agent.Requests[1].MaxCandidates}");   // only what is still missing - not another 20
    }

    [Fact]
    public async Task A_simulated_run_teaches_nothing_because_its_businesses_are_invented()
    {
        SetBatchSize(10);
        RecordRealRun(considered: 40, rejected: 36, duplicates: 0, model: "Simulated");    // would say 90% thrown away
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(15, _agent.Requests[0].MaxCandidates);                                // the starting guess (30%), not 90%
    }

    [Fact]
    public async Task Too_little_history_is_not_trusted_and_the_starting_guess_is_used()
    {
        SetBatchSize(10);
        RecordRealRun(considered: 5, rejected: 5, duplicates: 0);                          // 100% - but of only 5 candidates
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(15, _agent.Requests[0].MaxCandidates);
    }

    [Fact]
    public async Task Only_the_latest_runs_count_so_old_results_do_not_drag_the_estimate()
    {
        SetBatchSize(10);
        for (var i = 0; i < 5; i++)
            RecordRealRun(considered: 12, rejected: 0, duplicates: 0, at: DateTime.UtcNow.AddHours(5 - i));   // the five latest: nothing thrown away
        RecordRealRun(considered: 100, rejected: 90, duplicates: 0, at: DateTime.UtcNow.AddMinutes(30));     // older than those five: 90%
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(10, _agent.Requests[0].MaxCandidates);                                // 0% from the latest five: exactly what is needed
    }

    [Fact]
    public async Task Runs_from_before_the_profile_was_last_changed_are_ignored_because_other_rules_threw_away_other_things()
    {
        SetBatchSize(10);
        // A run under the old rules threw away 74% (as the Claude run of 17 Nov did) - then the profile was edited.
        RecordRealRun(considered: 31, rejected: 23, duplicates: 0, at: DateTime.UtcNow.AddDays(-2));
        await _db.SaveChangesAsync();
        ProfileLastChangedAt(DateTime.UtcNow.AddDays(-1));
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(15, _agent.Requests[0].MaxCandidates);                                // the starting guess, not 74% -> 20
    }

    [Fact]
    public async Task A_run_after_the_profile_was_last_changed_does_teach_it()
    {
        SetBatchSize(10);
        ProfileLastChangedAt(DateTime.UtcNow.AddDays(-1));
        RecordRealRun(considered: 12, rejected: 2, duplicates: 0, at: DateTime.UtcNow.AddHours(-3));   // since the change
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(12, _agent.Requests[0].MaxCandidates);
    }

    [Fact]
    public async Task Only_this_profiles_own_runs_teach_it_not_another_profiles()
    {
        SetBatchSize(10);
        RecordRealRun(considered: 40, rejected: 36, duplicates: 0, profileId: Guid.NewGuid());   // another profile: 90%
        await _db.SaveChangesAsync();
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(15, _agent.Requests[0].MaxCandidates);
    }

    [Fact]
    public async Task The_real_case_a_profile_edited_after_an_old_high_rejection_run_asks_for_15_and_fills_a_batch_of_10_with_nothing_left_over()
    {
        SetBatchSize(10);
        // Four earlier real runs of the same profile, 47 candidates and 23 thrown away (49%), all before the profile was last saved.
        RecordRealRun(considered: 31, rejected: 23, duplicates: 0, at: DateTime.UtcNow.AddDays(-2));
        RecordRealRun(considered: 8, rejected: 0, duplicates: 0, at: DateTime.UtcNow.AddDays(-2).AddHours(1));
        RecordRealRun(considered: 6, rejected: 0, duplicates: 0, at: DateTime.UtcNow.AddDays(-2).AddHours(2));
        RecordRealRun(considered: 2, rejected: 0, duplicates: 0, at: DateTime.UtcNow.AddDays(-2).AddHours(3));
        await _db.SaveChangesAsync();
        ProfileLastChangedAt(DateTime.UtcNow.AddHours(-6));
        var simulated = new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance);

        await ServiceWithChargeLog(new List<(string Key, string Note)>(), simulated).RunForTenantAsync(_tenant);

        var run = await _db.LeadDiscoveryRuns.OrderByDescending(r => r.RanAtUtc).FirstAsync(r => r.Model == "Simulated");
        Assert.Equal(15, run.CandidatesConsidered);                                        // asked for 15 - not 20 - and got 15
        // The earlier runs recorded above are other executions (and the test clock is fixed, so this one does not sort last by date).
        var execution = (await Executions()).Single(e => e.LockKey != "earlier-run");
        Assert.Equal(10, execution.CustomersCreated);
        Assert.Equal(0, await _db.LeadDiscoveryExecutionCustomers.CountAsync(c => c.ExecutionId == execution.Id && c.ErrorMessage!.Contains("batch was already full")));
        Assert.Equal(5, execution.CustomersInvalid);                                       // the ones a real search would have thrown away
    }

    [Fact]
    public async Task The_starting_guess_is_a_setting_that_changes_the_first_round_for_a_tenant_with_no_history()
    {
        SetBatchSize(10);
        _discoveryOptions.InitialRejectionRatePercent = 50;
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(20, _agent.Requests[0].MaxCandidates);                                // 10 / 0.5
    }

    [Fact]
    public async Task A_round_that_found_many_rejects_makes_the_next_round_ask_for_more()
    {
        SetBatchSize(10);
        _agent.Rounds.Add(Rejected(Letters.Take(15).ToArray()));                           // everything the first round found was rejected
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(15, _agent.Requests[0].MaxCandidates);                                // the 30% starting guess
        Assert.Equal(20, _agent.Requests[1].MaxCandidates);                                // now (3 + 15) / 25 = 72% -> 10 / 0.28 = 36, capped at 20
    }

    [Fact]
    public async Task A_round_with_no_rejects_makes_the_next_round_ask_for_little_more_than_it_still_needs()
    {
        SetBatchSize(10);
        _agent.Rounds.Add(Candidates(Letters.Take(6).ToArray()));                          // 6 usable, none thrown away: 4 still needed
        _agent.Rounds.Add(Candidates("Q", "R", "S", "T"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(15, _agent.Requests[0].MaxCandidates);
        Assert.Equal(5, _agent.Requests[1].MaxCandidates);                                 // (3 + 0) / 16 = 19% -> 4 / 0.81 = 4.9
    }

    [Fact]
    public async Task A_headroom_factor_of_one_asks_for_exactly_what_is_needed_every_round()
    {
        SetBatchSize(10);
        _discoveryOptions.CandidateHeadroomFactor = 1;
        _agent.Rounds.Add(Rejected(Letters.Take(10).ToArray()));
        _agent.Rounds.Add(Candidates(Letters.Take(10).ToArray()));

        await Service().RunForTenantAsync(_tenant);

        Assert.All(_agent.Requests.Take(2), r => Assert.Equal(10, r.MaxCandidates));
    }

    [Fact]
    public async Task Candidates_kept_only_as_leads_count_as_thrown_away_because_they_do_not_fill_the_batch()
    {
        SetBatchSize(4);
        // The first round's 4 are all refused by the DND check: kept as leads, none becomes a customer.
        _verifier.Decide = (id, _) => new PhoneVerificationItemResult(id, LeadDiscoveryVerificationStatus.Fail, "DND", null);
        _agent.Rounds.Add(Candidates("A", "B", "C", "D"));
        _agent.Rounds.Add(Candidates("E", "F", "G", "H"));

        await Service().RunForTenantAsync(_tenant);

        Assert.True(_agent.Requests[1].MaxCandidates > _agent.Requests[0].MaxCandidates,
            $"first {_agent.Requests[0].MaxCandidates}, second {_agent.Requests[1].MaxCandidates}");
    }

    [Fact]
    public async Task The_run_stops_after_two_rounds_that_find_nothing_at_all()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A"));                                                // one valid; then the agent has nothing more

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(1, await _db.Customers.CountAsync());
        Assert.Equal(3, _agent.Calls);                                                     // the round that found A, then two empty ones
    }

    [Fact]
    public async Task The_run_never_exceeds_the_round_limit_even_when_every_round_is_rejected()
    {
        SetBatchSize(2);
        _discoveryOptions.MaxRounds = 3;
        for (var i = 0; i < 10; i++)
            _agent.Rounds.Add(Rejected($"X{i}"));

        await Service().RunForTenantAsync(_tenant);

        Assert.Equal(3, _agent.Calls);
        Assert.Equal(0, await _db.Customers.CountAsync());
        Assert.Equal(0m, _quotaConsumed);                                                  // three rounds of research, nothing charged
        var run = await _db.LeadDiscoveryRuns.SingleAsync();
        Assert.Equal(3, run.Rounds);
        Assert.Equal(3, run.Rejected);
    }

    [Fact]
    public async Task Only_as_many_leads_are_taken_as_the_tenant_can_still_pay_for()
    {
        SetBatchSize(5);
        _agent.Rounds.Add(Candidates("A", "B", "C", "D", "E"));

        await Service(quotaAvailable: 2m).RunForTenantAsync(_tenant);

        Assert.Equal(2, await _db.Customers.CountAsync());
        Assert.Equal(2m, _quotaConsumed);
    }

    [Fact]
    public async Task A_customer_that_failed_and_is_created_by_the_retry_is_charged_once_then()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));
        var failure = FailCustomerSave("B Contact");
        await Service().RunForTenantAsync(_tenant);
        var first = await SingleExecutionAsync();
        Assert.Equal(1m, _quotaConsumed);                                                  // A only: B was not created

        failure.Enabled = false;
        await Service().RetryExecutionAsync(_tenant, first.Id);

        Assert.Equal(2, await _db.Customers.CountAsync());
        Assert.Equal(2m, _quotaConsumed);                                                  // B charged when it came to exist
    }

    [Fact]
    public async Task A_retry_of_a_run_that_was_charged_per_candidate_does_not_charge_its_customers_again()
    {
        SetBatchSize(2);
        _agent.Rounds.Add(Candidates("A", "B"));
        var failure = FailCustomerSave("B Contact");
        await Service().RunForTenantAsync(_tenant);
        var first = await SingleExecutionAsync();
        var consumedBefore = _quotaConsumed;

        // The earlier execution was charged the old way: one entry for every candidate, when it was researched.
        _db.QuotaLedgerEntries.Add(new QuotaLedgerEntry
        {
            TenantId = _tenant, QuotaType = QuotaType.Credits, EntryType = QuotaEntryType.Consumption, UnitsDelta = -3m,
            OperationKey = $"lead:{first.RootExecutionId:N}:r1", ReferenceType = "LeadDiscoveryRun", ReferenceId = first.Id.ToString("N")
        });
        await _db.SaveChangesAsync();

        failure.Enabled = false;
        await Service().RetryExecutionAsync(_tenant, first.Id);

        Assert.Equal(2, await _db.Customers.CountAsync());                                 // B was created by the retry...
        Assert.Equal(consumedBefore, _quotaConsumed);                                      // ...and not charged a second time
    }

    private LeadDiscoveryRunService ServiceWithChargeLog(List<(string Key, string Note)> charges, ILeadDiscoveryAgent? agent = null)
    {
        var planLimits = Stub<IPlanLimitsService>.Create(new() { ["GetLeadDiscoveryBatchLimitAsync"] = _ => Task.FromResult<int?>(null) });
        var quota = Stub<IQuotaGate>.Create(new()
        {
            ["GetAvailableAsync"] = _ => Task.FromResult(1000m),
            ["ConsumeLeadCandidatesAsync"] = args =>
            {
                _quotaConsumed += (decimal)args![1]!;
                charges.Add(((string)args![2]!, (string)args![4]!));
                return Task.FromResult((decimal)args![1]!);
            }
        });
        return new LeadDiscoveryRunService(
            _db, agent ?? _agent, planLimits, quota, Stub<IQuotaAlertService>.Create(new()),
            _clock, new IstTimeZone(_clock), Stub<ICampaignService>.Create(new()),
            _sqlLockStore, new ApplicationInstance(), NullLogger<LeadDiscoveryRunService>.Instance,
            new Snapshot<LeadDiscoveryOptions>(_discoveryOptions), new Snapshot<LeadDiscoveryPricingOptions>(new LeadDiscoveryPricingOptions()), _verifier, _consent,
            null);
    }

    private sealed class FakeAgent : ILeadDiscoveryAgent
    {
        public List<LeadDiscoveryAgentResult> Rounds { get; } = new();
        public List<LeadDiscoveryAgentRequest> Requests { get; } = new();
        public int Calls { get; private set; }

        public Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request with { KnownBusinesses = request.KnownBusinesses.ToList() });   // a snapshot: the service keeps adding to its own list
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
