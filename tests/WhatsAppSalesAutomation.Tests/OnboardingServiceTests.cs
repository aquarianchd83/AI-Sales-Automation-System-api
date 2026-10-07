using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Onboarding;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Packages;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Tenant onboarding: nine sequential, weighted steps, each judged by real data as it is now (delete a
/// package and its step is open again). Test names follow the acceptance criteria where one applies.</summary>
public sealed class OnboardingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly FakeWhatsApp _whatsApp = new();
    private readonly OnboardingService _service;

    public OnboardingServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Confianza IT", Slug = "confianza", Status = TenantStatus.Active });
        _db.SaveChanges();

        _service = new OnboardingService(_db, new Ambient(_tenant), new AnonymousUser(), _whatsApp, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- fixtures: the data each step produces ---------------------------------------------------------------------

    private async Task CompleteProfileAsync()
    {
        var tenant = await _db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.Industry = "Technology & software";
        tenant.BusinessDescription = "We build sales software.";
        tenant.SupportEmail = "hello@confianza.test";
        tenant.SupportPhone = "+91 98765 43210";
        tenant.CountryCode = "IN";
        await _db.SaveChangesAsync();
    }

    private async Task SubscribeAsync()
    {
        var plan = new Plan { Code = "silver", Name = "Silver", IsActive = true };
        _db.Plans.Add(plan);
        _db.Subscriptions.Add(new Subscription { TenantId = _tenant, PlanId = plan.Id, Status = SubscriptionStatus.Active });
        await _db.SaveChangesAsync();
    }

    private async Task AddPackageAsync()
    {
        _db.SalesPackages.Add(new SalesPackage { Name = "Gold", Price = 5000m });
        await _db.SaveChangesAsync();
    }

    private async Task AddLeadDiscoveryProfileAsync()
    {
        _db.LeadDiscoveryProfiles.Add(new LeadDiscoveryProfile { TargetBusinessType = "Eye clinic", Locations = new() { "Mohali" } });
        await _db.SaveChangesAsync();
    }

    private void ConnectWhatsApp(bool verified) =>
        _whatsApp.Config = new TenantWhatsAppConfigDto("123", "456", true, true, "v19.0", "https://graph.facebook.com/", true, false, null,
            VerifiedAtUtc: verified ? _clock.UtcNow : null);

    private async Task AddTemplateAsync(WhatsAppTemplateStatus status = WhatsAppTemplateStatus.Pending)
    {
        _db.MessageTemplates.Add(new MessageTemplate { Name = $"welcome_{status}", WhatsAppTemplateName = $"welcome_{status}".ToLowerInvariant(), BodyText = "Hi", WhatsAppTemplateStatus = status });
        await _db.SaveChangesAsync();
    }

    private async Task<Customer> AddCustomerAsync()
    {
        var customer = new Customer { PhoneNumberE164 = "+919876543210", FirstName = "Asha" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    private async Task AddCampaignAsync(Customer customer)
    {
        var campaign = new Campaign { Name = "Launch" };
        campaign.Steps.Add(new CampaignStep { StepNumber = 1, MessageText = "Hi" });
        campaign.CampaignCustomers.Add(new CampaignCustomer { CustomerId = customer.Id });
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync();
    }

    private async Task AddProcessedVoucherAsync()
    {
        var article = new KnowledgeBaseArticle { TenantId = _tenant, ArticleKey = "voucher", Title = "Voucher", Content = "10% off", ContentHash = "h" };
        _db.KnowledgeBaseArticles.Add(article);
        _db.KnowledgeIngestionJobs.Add(new KnowledgeIngestionJob { TenantId = _tenant, ArticleId = article.Id, ArticleVersionNumber = 1, State = KnowledgeIngestionState.Completed });
        await _db.SaveChangesAsync();
    }

    private async Task CompleteEverythingAsync()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await AddPackageAsync();
        await AddLeadDiscoveryProfileAsync();
        ConnectWhatsApp(verified: true);
        await AddTemplateAsync();
        await AddCampaignAsync(await AddCustomerAsync());
        await AddProcessedVoucherAsync();
    }

    private async Task<OnboardingStatusDto> StatusAsync() => (await _service.GetStatusAsync())!;

    private static OnboardingStepState StateOf(OnboardingStatusDto status, string key) => status.Steps.Single(s => s.Key == key).State;

    // ---- the catalog -------------------------------------------------------------------------------------------------

    [Fact]
    public void AC02_AC04_There_are_nine_steps_in_the_defined_order_and_their_weights_add_up_to_100()
    {
        Assert.Equal(
            new[] { "profile", "plan", "customer-package", "whatsapp", "message-template", "customer", "campaign", "lead-discovery", "knowledge-base" },
            OnboardingCatalog.Steps.Select(s => s.Key));
        Assert.Equal(new[] { 10, 10, 15, 15, 10, 10, 10, 10, 10 }, OnboardingCatalog.Steps.Select(s => s.Weight));
        Assert.Equal(100, OnboardingCatalog.Steps.Sum(s => s.Weight));
    }

    // ---- sequence and progress ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AC01_A_new_tenant_starts_on_the_profile_with_everything_after_it_waiting()
    {
        var status = await StatusAsync();

        Assert.False(status.IsCompleted);
        Assert.Equal(0, status.ProgressPercent);
        Assert.Equal("profile", status.CurrentStepKey);
        Assert.All(status.Steps.Skip(1), s => Assert.Equal(OnboardingStepState.Pending, s.State));
        Assert.Contains("industry", status.Steps[0].Missing);
    }

    [Fact]
    public async Task AC04_Progress_is_the_sum_of_the_weights_of_the_completed_steps_not_a_count()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await AddPackageAsync();

        var status = await StatusAsync();

        Assert.Equal(35, status.ProgressPercent); // 10 + 10 + 15, as in the requirement's own example
        Assert.Equal("whatsapp", status.CurrentStepKey);
    }

    [Fact]
    public async Task Every_step_is_judged_on_its_own_data_not_only_up_to_the_first_gap()
    {
        await CompleteEverythingAsync();
        _db.SalesPackages.RemoveRange(await _db.SalesPackages.ToListAsync());
        await _db.SaveChangesAsync();

        var status = await StatusAsync();

        Assert.Equal(85, status.ProgressPercent); // everything but the 15% package step
        Assert.Equal("customer-package", status.CurrentStepKey);
        Assert.Equal(OnboardingStepState.Completed, StateOf(status, "campaign"));
        Assert.Equal(OnboardingStepState.Completed, StateOf(status, "knowledge-base"));
        Assert.Equal(1, status.Steps.Count(s => s.State == OnboardingStepState.Current));
        Assert.DoesNotContain(status.Steps, s => s.State == OnboardingStepState.Pending);
    }

    [Fact]
    public async Task AC03_Only_the_next_step_to_do_is_open_the_ones_after_it_wait()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await AddCustomerAsync(); // data for a later step, with the package step still to do

        var status = await StatusAsync();

        Assert.Equal(OnboardingStepState.Current, StateOf(status, "customer-package"));
        Assert.Equal(OnboardingStepState.Pending, StateOf(status, "whatsapp"));
        Assert.Equal(OnboardingStepState.Pending, StateOf(status, "lead-discovery"));
        Assert.Equal(OnboardingStepState.Completed, StateOf(status, "customer")); // it has its data, so it is done
        Assert.Equal(30, status.ProgressPercent); // 10 + 10 + 10
        Assert.NotNull(status.Steps.Single(s => s.Key == "whatsapp").Missing); // and it says what it needs
    }

    // ---- persistence and resume ------------------------------------------------------------------------------------

    [Fact]
    public async Task AC05_Completed_steps_are_stored_and_resume_where_the_tenant_left_off()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await StatusAsync(); // signed out here

        var resumed = await StatusAsync(); // signed back in

        Assert.Equal(20, resumed.ProgressPercent);
        Assert.Equal("customer-package", resumed.CurrentStepKey);
        Assert.Equal(2, await _db.TenantOnboardingSteps.CountAsync());
    }

    [Fact]
    public async Task Deleting_the_only_package_reopens_that_step_and_only_that_one()
    {
        await CompleteEverythingAsync();
        var done = await StatusAsync();
        Assert.True(done.IsCompleted);
        Assert.Equal(100, done.ProgressPercent);

        _db.SalesPackages.RemoveRange(await _db.SalesPackages.ToListAsync());
        await _db.SaveChangesAsync();
        var reopened = await StatusAsync();

        Assert.False(reopened.IsCompleted);
        Assert.Null(reopened.CompletedAt);
        Assert.Equal("customer-package", reopened.CurrentStepKey);
        Assert.Equal(85, reopened.ProgressPercent); // 100 less the 15% package step; the rest still have their data
        Assert.Equal(OnboardingStepState.Completed, StateOf(reopened, "campaign"));
        Assert.Null((await _db.Tenants.SingleAsync(t => t.Id == _tenant)).OnboardingCompletedAt);
        Assert.Equal(8, await _db.TenantOnboardingSteps.CountAsync());
    }

    [Fact]
    public async Task Creating_the_package_again_completes_everything_that_is_still_in_place()
    {
        await CompleteEverythingAsync();
        _db.SalesPackages.RemoveRange(await _db.SalesPackages.ToListAsync());
        await _db.SaveChangesAsync();
        Assert.False((await StatusAsync()).IsCompleted);

        await AddPackageAsync();
        var back = await StatusAsync();

        Assert.True(back.IsCompleted);
        Assert.Equal(100, back.ProgressPercent);
    }

    [Fact]
    public async Task A_step_that_stops_being_met_is_open_again_even_in_the_middle()
    {
        await CompleteEverythingAsync();
        await StatusAsync();

        var tenant = await _db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.Industry = null;
        await _db.SaveChangesAsync();
        var status = await StatusAsync();

        Assert.Equal("profile", status.CurrentStepKey);
        Assert.Equal(90, status.ProgressPercent);
        Assert.Contains("industry", status.Steps[0].Missing);
    }

    [Fact]
    public async Task Lead_discovery_comes_after_the_campaign_and_before_the_voucher()
    {
        await CompleteEverythingAsync();
        _db.LeadDiscoveryProfiles.RemoveRange(await _db.LeadDiscoveryProfiles.ToListAsync());
        await _db.SaveChangesAsync();

        var status = await StatusAsync();

        Assert.Equal("lead-discovery", status.CurrentStepKey);
        Assert.Equal(90, status.ProgressPercent);
        Assert.Equal(OnboardingStepState.Completed, StateOf(status, "campaign")); // the step before it
        Assert.Equal(OnboardingStepState.Completed, StateOf(status, "knowledge-base")); // the one after it keeps its data
        Assert.Equal(8, status.Steps.ToList().FindIndex(s => s.Key == "knowledge-base"));
        Assert.Equal(7, status.Steps.ToList().FindIndex(s => s.Key == "lead-discovery"));
    }

    // ---- the plan step: a free trial counts ------------------------------------------------------------------------

    [Fact]
    public async Task A_tenant_on_its_free_trial_has_the_plan_step_without_choosing_or_upgrading()
    {
        var tenant = await _db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.Status = TenantStatus.Trial;
        tenant.TrialEndsAtUtc = _clock.UtcNow.AddDays(10);
        await _db.SaveChangesAsync();
        await CompleteProfileAsync();

        var status = await StatusAsync();

        Assert.Equal(OnboardingStepState.Completed, StateOf(status, "plan")); // no subscription at all
        Assert.Equal("customer-package", status.CurrentStepKey);
        Assert.Equal(20, status.ProgressPercent);
    }

    [Fact]
    public async Task Once_the_trial_has_ended_with_no_plan_chosen_the_step_is_open_again()
    {
        var tenant = await _db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.Status = TenantStatus.Trial;
        tenant.TrialEndsAtUtc = _clock.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync();
        await CompleteProfileAsync();

        var status = await StatusAsync();

        Assert.Equal("plan", status.CurrentStepKey);
        Assert.Contains("trial has ended", status.Steps.Single(s => s.Key == "plan").Missing);
    }

    [Fact]
    public async Task An_active_tenant_still_needs_a_plan()
    {
        await CompleteProfileAsync(); // the fixture tenant is Active, not on a trial

        Assert.Equal("plan", (await StatusAsync()).CurrentStepKey);
    }

    // ---- individual step rules -------------------------------------------------------------------------------------

    // ---- step 4: only the number -------------------------------------------------------------------------------------

    private async Task ReachWhatsAppStepAsync()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await AddPackageAsync();
    }

    [Fact]
    public async Task WhatsApp_asks_only_for_the_number_no_credentials_and_no_verification()
    {
        await ReachWhatsAppStepAsync();

        var asked = await StatusAsync();
        Assert.Equal("whatsapp", asked.CurrentStepKey);
        Assert.Equal("Enter the WhatsApp number your customers will message.", asked.Steps.Single(s => s.Key == "whatsapp").Missing);

        var tenant = await _db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.WhatsAppNumber = "+91 98765 43210";
        await _db.SaveChangesAsync();

        var done = await StatusAsync(); // no connection row at all: the platform administrator connects it later
        Assert.Equal(OnboardingStepState.Completed, StateOf(done, "whatsapp"));
        Assert.Equal("message-template", done.CurrentStepKey);
    }

    [Fact]
    public async Task Saved_credentials_that_were_never_verified_do_not_stand_in_for_the_number()
    {
        await ReachWhatsAppStepAsync();
        ConnectWhatsApp(verified: false);

        Assert.Equal("whatsapp", (await StatusAsync()).CurrentStepKey);
    }

    [Fact]
    public async Task A_tenant_whose_connection_is_already_verified_is_not_asked_for_the_number_again()
    {
        await ReachWhatsAppStepAsync();
        ConnectWhatsApp(verified: true);

        Assert.Equal(OnboardingStepState.Completed, StateOf(await StatusAsync(), "whatsapp"));
    }

    [Fact]
    public async Task Clearing_the_number_reopens_the_step()
    {
        await ReachWhatsAppStepAsync();
        var tenant = await _db.Tenants.SingleAsync(t => t.Id == _tenant);
        tenant.WhatsAppNumber = "+91 98765 43210";
        await _db.SaveChangesAsync();
        Assert.Equal(OnboardingStepState.Completed, StateOf(await StatusAsync(), "whatsapp"));

        tenant.WhatsAppNumber = null;
        await _db.SaveChangesAsync();

        Assert.Equal("whatsapp", (await StatusAsync()).CurrentStepKey);
    }

    [Fact]
    public async Task A_rejected_template_does_not_complete_the_template_step_but_a_submitted_one_does()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await AddPackageAsync();
        await AddLeadDiscoveryProfileAsync();
        ConnectWhatsApp(verified: true);

        await AddTemplateAsync(WhatsAppTemplateStatus.Rejected);
        Assert.Equal("message-template", (await StatusAsync()).CurrentStepKey);

        await AddTemplateAsync(WhatsAppTemplateStatus.Pending);
        Assert.Equal(OnboardingStepState.Completed, StateOf(await StatusAsync(), "message-template"));
    }

    [Fact]
    public async Task A_campaign_counts_only_with_a_message_step_and_a_customer()
    {
        await CompleteProfileAsync();
        await SubscribeAsync();
        await AddPackageAsync();
        await AddLeadDiscoveryProfileAsync();
        ConnectWhatsApp(verified: true);
        await AddTemplateAsync();
        var customer = await AddCustomerAsync();

        _db.Campaigns.Add(new Campaign { Name = "Empty" });
        await _db.SaveChangesAsync();
        Assert.Equal("campaign", (await StatusAsync()).CurrentStepKey);

        await AddCampaignAsync(customer);
        Assert.Equal(OnboardingStepState.Completed, StateOf(await StatusAsync(), "campaign"));
    }

    [Fact]
    public async Task AC11_The_voucher_counts_only_once_it_has_been_processed()
    {
        await CompleteEverythingAsync();
        var job = await _db.KnowledgeIngestionJobs.SingleAsync();
        job.State = KnowledgeIngestionState.Embedding;
        await _db.SaveChangesAsync();

        Assert.Equal("knowledge-base", (await StatusAsync()).CurrentStepKey);

        job.State = KnowledgeIngestionState.Completed;
        await _db.SaveChangesAsync();
        Assert.True((await StatusAsync()).IsCompleted);
    }

    // ---- completion --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AC12_When_every_step_is_done_progress_is_100_and_onboarding_is_completed_for_good()
    {
        await CompleteEverythingAsync();

        var status = await StatusAsync();

        Assert.True(status.IsCompleted);
        Assert.Equal(100, status.ProgressPercent);
        Assert.Null(status.CurrentStepKey);
        Assert.Equal(_clock.UtcNow, status.CompletedAt);
        Assert.Equal(_clock.UtcNow, (await _db.Tenants.SingleAsync(t => t.Id == _tenant)).OnboardingCompletedAt);
    }

    [Fact]
    public async Task A_caller_with_no_tenant_has_nothing_to_onboard()
    {
        var platform = new OnboardingService(_db, new Ambient(null), new AnonymousUser(), _whatsApp, _clock);

        Assert.Null(await platform.GetStatusAsync());
    }

    // ---- fakes -------------------------------------------------------------------------------------------------------

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid? tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => TenantId is null;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    /// <summary>Only the read onboarding needs; WhatsApp's own storage and verification have their own tests.</summary>
    private sealed class FakeWhatsApp : ITenantWhatsAppConfigProvider
    {
        public TenantWhatsAppConfigDto? Config { get; set; }

        public Task<TenantWhatsAppConfigDto?> GetConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Config);

        public Task<TenantWhatsAppConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(Config);

        public Task<TenantWhatsAppCredentials?> GetForCurrentTenantAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TenantWhatsAppLookupResult?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TenantWhatsAppConfigDto> SaveConfigForCurrentTenantAsync(UpdateTenantWhatsAppConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TenantWhatsAppConfigDto> SaveConfigForTenantAsync(Guid tenantId, UpdateTenantWhatsAppConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TenantWhatsAppConnectionSummary>> GetAllConnectionSummariesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
