using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>An AI reply in the credit history says only "ConversationReply". For the tenant's own history it also carries the start of the customer's message
/// the reply answered, so the entry says which conversation it was.</summary>
public sealed class LedgerConversationTextTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 7, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "T", Slug = "t" };
    private readonly Tenant _other = new() { Name = "Other", Slug = "other" };
    private readonly Customer _customer;
    private readonly Customer _otherCustomer;

    public LedgerConversationTextTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _customer = new Customer { TenantId = _tenant.Id, PhoneNumberE164 = "+919000000001" };
        _otherCustomer = new Customer { TenantId = _other.Id, PhoneNumberE164 = "+919000000002" };
        _db.Tenants.AddRange(_tenant, _other);
        _db.Customers.AddRange(_customer, _otherCustomer);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private QuotaLedgerService Ledger() => new(_db, new TestClock());

    /// <summary>A customer message, the AI transaction that answered it, and the credit entry that paid for it.</summary>
    private async Task<Guid> AddReplyAsync(string? customerText, Tenant? owner = null, Customer? customer = null, bool linkTransaction = true)
    {
        owner ??= _tenant;
        customer ??= _customer;
        var message = new Message
        {
            TenantId = owner.Id, CustomerId = customer.Id, Direction = MessageDirection.Inbound, MessageType = MessageType.Text, Text = customerText,
            IdempotencyKey = Guid.NewGuid().ToString(), CreatedAt = Now
        };
        var transaction = new AiTransaction
        {
            TenantId = owner.Id, Operation = "ConversationReply", ReferenceId = linkTransaction ? message.Id.ToString() : "not-a-message", OperationKey = "ai:" + Guid.NewGuid(),
            Provider = "OpenAI", Model = "gpt-5-nano", RequestedAtUtc = Now
        };
        var entry = new QuotaLedgerEntry
        {
            TenantId = owner.Id, QuotaType = QuotaType.Credits, EntryType = QuotaEntryType.Consumption, UnitsDelta = -1, BalanceAfter = 9,
            Feature = PlatformFeature.AiConversations, ReferenceType = "AiTransaction", ReferenceId = transaction.Id.ToString(), Note = "ConversationReply",
            OperationKey = transaction.OperationKey, OccurredAtUtc = Now
        };
        _db.Messages.Add(message);
        _db.AiTransactions.Add(transaction);
        _db.QuotaLedgerEntries.Add(entry);
        await _db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<QuotaLedgerEntryDto> EntryAsync(Guid id, bool withText)
    {
        var page = await Ledger().GetLedgerAsync(_tenant.Id, new QuotaLedgerQuery { WithConversationText = withText });
        return page.Items.Single(i => i.Id == id);
    }

    [Fact]
    public async Task A_reply_entry_carries_the_start_of_the_customer_message_it_answered()
    {
        var id = await AddReplyAsync("Hi, I want to know about the nayan care platform");

        Assert.Equal("Hi, I want to know about the nayan care platform", (await EntryAsync(id, withText: true)).CustomerText);
    }

    [Fact]
    public async Task A_long_message_is_cut_at_a_word_and_flattened_to_one_line()
    {
        var id = await AddReplyAsync("Hello   there,\nI run a small clinic with three doctors and I would like to know how your platform handles OPD and IPD billing for patients");

        var text = (await EntryAsync(id, withText: true)).CustomerText!;

        Assert.EndsWith("…", text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain("  ", text);
        Assert.True(text.Length <= 91, text);
        // Cut between words, never in the middle of one.
        Assert.StartsWith("Hello there, I run a small clinic with three doctors and I would like to know how your", text);
        Assert.DoesNotContain("platfo…", text);
    }

    [Fact]
    public async Task Without_asking_for_it_the_entry_carries_no_customer_text()
    {
        var id = await AddReplyAsync("Hi, I want to know about the nayan care platform");

        Assert.Null((await EntryAsync(id, withText: false)).CustomerText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_message_with_no_text_leaves_the_entry_as_it_was(string? text)
    {
        var id = await AddReplyAsync(text);

        Assert.Null((await EntryAsync(id, withText: true)).CustomerText);
    }

    [Fact]
    public async Task An_entry_that_cannot_be_followed_to_a_message_is_left_as_it_was()
    {
        var id = await AddReplyAsync("Hi there", linkTransaction: false);

        var entry = await EntryAsync(id, withText: true);

        Assert.Null(entry.CustomerText);
        Assert.Equal("ConversationReply", entry.Note);
    }

    [Fact]
    public async Task Only_ai_reply_entries_are_followed()
    {
        _db.QuotaLedgerEntries.Add(new QuotaLedgerEntry
        {
            TenantId = _tenant.Id, QuotaType = QuotaType.Credits, EntryType = QuotaEntryType.Purchase, UnitsDelta = 100, BalanceAfter = 100,
            OperationKey = "buy-1", OccurredAtUtc = Now
        });
        await _db.SaveChangesAsync();

        var page = await Ledger().GetLedgerAsync(_tenant.Id, new QuotaLedgerQuery { WithConversationText = true });

        Assert.All(page.Items, i => Assert.Null(i.CustomerText));
    }

    [Fact]
    public async Task Another_tenants_message_is_never_read()
    {
        // This tenant's entry points at a transaction whose message belongs to someone else: nothing is shown.
        var theirs = new Message
        {
            TenantId = _other.Id, CustomerId = _otherCustomer.Id, Direction = MessageDirection.Inbound, MessageType = MessageType.Text, Text = "a private question",
            IdempotencyKey = Guid.NewGuid().ToString(), CreatedAt = Now
        };
        var transaction = new AiTransaction
        {
            TenantId = _tenant.Id, Operation = "ConversationReply", ReferenceId = theirs.Id.ToString(), OperationKey = "ai:" + Guid.NewGuid(),
            Provider = "OpenAI", Model = "gpt-5-nano", RequestedAtUtc = Now
        };
        var entry = new QuotaLedgerEntry
        {
            TenantId = _tenant.Id, QuotaType = QuotaType.Credits, EntryType = QuotaEntryType.Consumption, UnitsDelta = -1, BalanceAfter = 9,
            Feature = PlatformFeature.AiConversations, ReferenceType = "AiTransaction", ReferenceId = transaction.Id.ToString(), OperationKey = transaction.OperationKey, OccurredAtUtc = Now
        };
        _db.Messages.Add(theirs);
        _db.AiTransactions.Add(transaction);
        _db.QuotaLedgerEntries.Add(entry);
        await _db.SaveChangesAsync();

        Assert.Null((await EntryAsync(entry.Id, withText: true)).CustomerText);
    }
}
