using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What the agent is told the CRM already knows, before the customer has said anything.</summary>
public sealed class CrmContextBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly CrmContextBuilder _builder;

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly Guid _currentLeadId = Guid.NewGuid();
    private readonly Guid _oldLeadId = Guid.NewGuid();
    private readonly Dictionary<string, Guid> _fieldIds = new();

    private static readonly IReadOnlySet<string> NothingKnown = new HashSet<string>();

    public CrmContextBuilderTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _builder = new CrmContextBuilder(_db);

        _db.Customers.Add(new Customer { Id = _customerId, TenantId = _tenant, FirstName = "Asha", PhoneNumberE164 = "+919000000001", Source = "Lead discovery" });
        _db.Conversations.Add(new Conversation { Id = _conversationId, TenantId = _tenant, CustomerId = _customerId });
        _db.Leads.Add(new Lead { Id = _currentLeadId, TenantId = _tenant, CustomerId = _customerId });
        _db.Leads.Add(new Lead { Id = _oldLeadId, TenantId = _tenant, CustomerId = _customerId, Stage = LeadStage.Lost });

        foreach (var seed in QualificationDefaults.Fields)
        {
            var field = new QualificationField
            {
                TenantId = _tenant, FieldKey = seed.FieldKey, DisplayName = seed.DisplayName, Question = seed.Question,
                DataType = seed.DataType, IsRequired = seed.IsRequired, Priority = seed.Priority, SortOrder = seed.SortOrder
            };
            _fieldIds[seed.FieldKey] = field.Id;
            _db.QualificationFields.Add(field);
        }

        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<AiCrmContext> Build(IReadOnlySet<string>? known = null) =>
        _builder.BuildAsync(_customerId, _currentLeadId, _conversationId, known ?? NothingKnown, minConfidence: 0.6);

    private void AddOldAnswer(string key, string value, double confidence = 1.0, bool superseded = false)
    {
        _db.LeadQualificationValues.Add(new LeadQualificationValue
        {
            TenantId = _tenant, LeadId = _oldLeadId, FieldId = _fieldIds[key], FieldKey = key, RawValue = value,
            ExtractionConfidence = confidence, IsSuperseded = superseded
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task A_customer_with_nothing_on_file_has_an_empty_context_apart_from_their_source()
    {
        var crm = await Build();

        Assert.Equal("Lead discovery", crm.CustomerSource);
        Assert.Empty(crm.Tags);
        Assert.Null(crm.CampaignName);
        Assert.Empty(crm.EarlierAnswers);
        Assert.Equal(0, crm.PreviousConversations);
    }

    [Fact]
    public async Task Tags_are_carried_into_the_context()
    {
        var tag = new CustomerTag { TenantId = _tenant, Name = "VIP" };
        tag.Customers.Add((await _db.Customers.SingleAsync()));
        _db.CustomerTags.Add(tag);
        await _db.SaveChangesAsync();

        var crm = await Build();

        Assert.Equal(new[] { "VIP" }, crm.Tags);
    }

    [Fact]
    public async Task The_last_campaign_message_and_its_campaign_say_what_the_customer_is_replying_to()
    {
        var campaign = new Campaign { TenantId = _tenant, Name = "Diwali offer", CreatedBy = Guid.NewGuid() };
        var enrolment = new CampaignCustomer { TenantId = _tenant, CampaignId = campaign.Id, CustomerId = _customerId };
        _db.Campaigns.Add(campaign);
        _db.CampaignCustomers.Add(enrolment);
        _db.Messages.Add(new Message
        {
            TenantId = _tenant, CustomerId = _customerId, ConversationId = _conversationId, CampaignCustomerId = enrolment.Id,
            Direction = MessageDirection.Outbound, Text = "Diwali   offer: 10% off\nthis week only.", IdempotencyKey = "c1"
        });
        await _db.SaveChangesAsync();

        var crm = await Build();

        Assert.Equal("Diwali offer", crm.CampaignName);
        Assert.Equal("Diwali offer: 10% off this week only.", crm.LastCampaignMessage);
    }

    [Fact]
    public async Task An_earlier_answer_is_offered_for_confirmation_unless_this_lead_already_has_one()
    {
        AddOldAnswer("budget", "50 lakh");
        AddOldAnswer("interest", "2BHK in Pune");

        var crm = await Build(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "budget" });

        var answer = Assert.Single(crm.EarlierAnswers);
        Assert.Equal("interest", answer.FieldKey);
        Assert.Equal("2BHK in Pune", answer.RawValue);
    }

    [Fact]
    public async Task A_guess_or_a_superseded_answer_is_not_resurfaced_as_a_fact()
    {
        AddOldAnswer("budget", "maybe 5 lakh", confidence: 0.3);
        AddOldAnswer("interest", "1BHK", superseded: true);

        var crm = await Build();

        Assert.Empty(crm.EarlierAnswers);
    }

    [Fact]
    public async Task Earlier_conversations_are_counted_but_the_current_one_is_not()
    {
        _db.Conversations.Add(new Conversation { TenantId = _tenant, CustomerId = _customerId, Status = ConversationStatus.Closed });
        await _db.SaveChangesAsync();

        var crm = await Build();

        Assert.Equal(1, crm.PreviousConversations);
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
