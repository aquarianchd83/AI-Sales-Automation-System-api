using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The prepaid rule end to end through the real campaign sender: no quota, no send; a failed send
/// costs nothing; and a send costs its template category's weight.</summary>
public sealed class CampaignSendQuotaTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Clock _clock = new() { UtcNow = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc) };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly FakeWhatsApp _whatsApp = new();
    private readonly CampaignSendService _sender;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Guid _conversationId = Guid.NewGuid();
    private Campaign _campaign = null!;

    public CampaignSendQuotaTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        var gate = new QuotaGate(_ledger, new FixedOptions<WhatsAppPricingOptions>(new()), new FixedOptions<TrialQuotaOptions>(new()));

        var conversations = DispatchProxy.Create<IConversationService, Stub>();
        ((Stub)(object)conversations).Handler = (m, _) => m.Name == nameof(IConversationService.GetOrCreateActiveConversationIdAsync)
            ? Task.FromResult(_conversationId)
            : throw new NotImplementedException(m.Name);

        var config = DispatchProxy.Create<ITenantConfigOverrideProvider, Stub>();
        ((Stub)(object)config).Handler = (m, _) => m.Name == nameof(ITenantConfigOverrideProvider.GetMessagingOptionsAsync)
            ? Task.FromResult(new MessagingOptions())
            : throw new NotImplementedException(m.Name);

        _sender = new CampaignSendService(
            _db, _whatsApp, _clock, conversations, new AmbientTenant(_tenant.Id), gate, config,
            new LocalClock(_clock), NullLogger<CampaignSendService>.Instance);

        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Seed(TemplateCategory category, int customers)
    {
        var customer0 = new Customer { PhoneNumberE164 = "+919000000000", OptInStatus = OptInStatus.OptedIn };
        _db.Customers.Add(customer0);
        _db.Conversations.Add(new Conversation { Id = _conversationId, CustomerId = customer0.Id });

        var template = new MessageTemplate
        {
            Name = "t", WhatsAppTemplateName = "t", Category = category, BodyText = "Hi",
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved
        };
        _db.MessageTemplates.Add(template);

        _campaign = new Campaign { Name = "c", Status = CampaignStatus.Running, CreatedBy = Guid.NewGuid(), StartedAt = _clock.UtcNow };
        _campaign.Steps.Add(new CampaignStep { StepNumber = 0, MessageText = "Hi", MessageTemplateId = template.Id });
        _db.Campaigns.Add(_campaign);

        for (var i = 0; i < customers; i++)
        {
            var customer = i == 0 ? customer0 : new Customer { PhoneNumberE164 = $"+91900000000{i}", OptInStatus = OptInStatus.OptedIn };
            if (i > 0) _db.Customers.Add(customer);
            _db.CampaignCustomers.Add(new CampaignCustomer { CampaignId = _campaign.Id, CustomerId = customer.Id });
        }

        _db.SaveChanges();
    }

    private Task Fund(decimal units) =>
        _ledger.AdjustAsync(_tenant.Id, QuotaType.WhatsAppMessages, units, "test funding", Guid.NewGuid());

    private async Task<decimal> BalanceAsync() =>
        (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == QuotaType.WhatsAppMessages).Balance;

    [Fact]
    public async Task With_no_quota_nothing_is_sent_and_nothing_is_queued()
    {
        Seed(TemplateCategory.Marketing, customers: 2);

        var result = await _sender.ProcessInitialSendsAsync();

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, _whatsApp.Calls);
        Assert.Empty(await _db.Messages.ToListAsync());
    }

    [Fact]
    public async Task Each_marketing_send_spends_one_unit_and_the_campaign_stops_when_they_run_out()
    {
        Seed(TemplateCategory.Marketing, customers: 3);
        await Fund(2);

        var result = await _sender.ProcessInitialSendsAsync();

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, _whatsApp.Calls);
        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal(1, await _db.CampaignCustomers.CountAsync(c => c.Status == CampaignCustomerStatus.Pending));
    }

    [Fact]
    public async Task A_utility_template_costs_its_price_share_of_a_marketing_one()
    {
        Seed(TemplateCategory.Utility, customers: 1);
        await Fund(1);

        await _sender.ProcessInitialSendsAsync();

        // Utility is 0.004 against marketing's 0.025: 0.16 of a unit, so 1 unit leaves 0.84.
        Assert.Equal(0.84m, await BalanceAsync());
    }

    [Fact]
    public async Task A_send_that_fails_is_not_charged()
    {
        Seed(TemplateCategory.Marketing, customers: 1);
        await Fund(5);
        _whatsApp.Succeed = false;

        var result = await _sender.ProcessInitialSendsAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(5m, await BalanceAsync());
        Assert.Equal(MessageStatus.Failed, (await _db.Messages.SingleAsync()).Status);
    }

    [Fact]
    public async Task Retrying_a_failed_send_charges_only_once_it_finally_succeeds()
    {
        Seed(TemplateCategory.Marketing, customers: 1);
        await Fund(5);
        _whatsApp.Succeed = false;
        await _sender.ProcessInitialSendsAsync();

        _whatsApp.Succeed = true;
        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        var message = await _db.Messages.SingleAsync();
        message.NextAttemptAt = null;
        await _db.SaveChangesAsync();

        var retry = await _sender.RetryFailedSendsAsync();

        Assert.Equal(1, retry.Sent);
        Assert.Equal(4m, await BalanceAsync());
    }

    private sealed class FakeWhatsApp : IWhatsAppService
    {
        public bool Succeed { get; set; } = true;
        public int Calls { get; private set; }

        public Task<WhatsAppSendResult> SendTemplateMessageAsync(
            string toPhoneNumberE164, string templateName, string languageCode, IReadOnlyList<string> parameterValues,
            string? mediaUrl = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Succeed ? new WhatsAppSendResult(true, $"wamid.{Calls}", null) : new WhatsAppSendResult(false, null, "rejected"));
        }

        public Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (m, _) => throw new NotImplementedException(m.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class LocalClock : ITenantTimeZoneProvider
    {
        private readonly Clock _clock;
        public LocalClock(Clock clock) => _clock = clock;
        public Task<DateTime> GetLocalNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(_clock.IstNow);
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
