using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
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
    public async Task New_customers_are_created_once_with_whatsapp_opted_in()
    {
        _agent.Rounds.Add(Candidates("A", "B"));

        await Service().RunForTenantAsync(_tenant);

        var customers = await _db.Customers.OrderBy(c => c.FirstName).ToListAsync();
        Assert.Equal(new[] { "A Contact", "B Contact" }, customers.Select(c => c.FirstName));
        Assert.All(customers, c =>
        {
            Assert.Equal(OptInStatus.OptedIn, c.OptInStatus);
            Assert.NotNull(c.OptInTimestamp);
            Assert.Equal("Lead discovery", c.OptInSource);
            Assert.Equal(_tenant, c.TenantId);
        });

        var execution = await SingleExecutionAsync();
        Assert.Equal(LeadDiscoveryExecutionStatus.Completed, execution.Status);
        Assert.Equal(2, execution.CustomersCreated);
        Assert.Equal(new DateTime(2026, 9, 10), execution.ProcessingDate);
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
        Assert.Equal(new DateTime(2026, 9, 10, 10, 30, 0), generated.ScheduledStartAt);

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
        // Renew at every checkpoint, and have the provider report the lock lost on the third renewal:
        // round checkpoint (1), customer A (2), customer B (3 -> lost).
        _discoveryOptions.LockRenewWhenRemainingSeconds = _discoveryOptions.LockLeaseSeconds;
        var store = new ScriptedLockStore(_sqlLockStore) { LoseOnRenewal = 3 };
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
            BatchSize = 25, PhoneRequired = true, MinimumLeadScore = 60,
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

    private LeadDiscoveryRunService Service(ILeadDiscoveryLockStore? lockStore = null)
    {
        var planLimits = Stub<IPlanLimitsService>.Create(new()
        {
            ["GetLeadDiscoveryBatchLimitAsync"] = _ => Task.FromResult<int?>(null),
            ["EnsureCanCreateCampaignAsync"] = _ => _beforeCampaignCreate?.Invoke() ?? Task.CompletedTask
        });
        var quota = Stub<IQuotaGate>.Create(new()
        {
            ["GetAvailableAsync"] = _ => Task.FromResult(1000m),
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
            _db, _agent, planLimits, quota, _clock, new IstTimeZone(_clock), campaigns,
            lockStore ?? _sqlLockStore, new ApplicationInstance(), NullLogger<LeadDiscoveryRunService>.Instance,
            Options.Create(_discoveryOptions), new Snapshot<LeadDiscoveryPricingOptions>(new LeadDiscoveryPricingOptions()));
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
