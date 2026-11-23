using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The list endpoints' totals when the COUNT(*) is skipped for a short last page, the shared customer search, and the import's
/// set-based lookups of existing numbers and tags.
/// </summary>
public sealed class ListPagingAndImportTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly FakeImport _import = new();
    private int _phoneSeed;

    public ListPagingAndImportTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private CustomerService Customers() => new(
        _db, _import, new TestClock(),
        new CreateCustomerRequestValidator(), new UpdateCustomerRequestValidator(),
        new BulkDeleteCustomersRequestValidator(), new OptInCustomerRequestValidator(),
        new BulkAddCustomerTagsRequestValidator(), new Ambient(_tenant.Id));

    private ConversationService Conversations() => new(_db, null!, null!, null!, null!, null!, null!);

    private async Task<Customer> AddCustomerAsync(string firstName)
    {
        var customer = new Customer { PhoneNumberE164 = "+9190000" + (++_phoneSeed).ToString("D5"), FirstName = firstName };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    [Theory]
    [InlineData(1, 10, 7)] // short first page: total comes from the page itself
    [InlineData(2, 5, 7)]  // short last page
    [InlineData(1, 5, 7)]  // full page: still counted
    [InlineData(1, 7, 7)]  // exactly full page: counted
    [InlineData(3, 5, 7)]  // past the end: counted
    public async Task The_customer_total_is_right_whether_or_not_the_count_is_skipped(int page, int pageSize, int customers)
    {
        for (var i = 0; i < customers; i++)
            await AddCustomerAsync($"C{i}");

        var result = await Customers().GetPagedAsync(new PagedRequest { Page = page, PageSize = pageSize });

        Assert.Equal(customers, result.TotalCount);
        Assert.Equal(Math.Max(0, Math.Min(pageSize, customers - (page - 1) * pageSize)), result.Items.Count);
    }

    [Fact]
    public async Task An_empty_list_totals_zero()
    {
        var result = await Customers().GetPagedAsync(new PagedRequest { Page = 1, PageSize = 10, Search = "nobody" });

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Customer_search_matches_a_substring_of_name_phone_or_email()
    {
        var asha = await AddCustomerAsync("Asha");
        await AddCustomerAsync("Bela");
        var mailed = await AddCustomerAsync("Chitra");
        mailed.Email = "chitra.k@example.com";
        await _db.SaveChangesAsync();

        Assert.Equal(new[] { asha.Id }, (await Customers().GetPagedAsync(new PagedRequest { Search = " sha " })).Items.Select(c => c.Id));
        Assert.Equal(new[] { mailed.Id }, (await Customers().GetPagedAsync(new PagedRequest { Search = "k@example" })).Items.Select(c => c.Id));
        Assert.Equal(3, (await Customers().GetPagedAsync(new PagedRequest { Search = "+919000" })).TotalCount);
    }

    [Fact]
    public async Task Inbox_search_keeps_only_conversations_whose_customer_matches()
    {
        var asha = await AddCustomerAsync("Asha");
        var bela = await AddCustomerAsync("Bela");
        var ashaConversation = new Conversation { CustomerId = asha.Id };
        _db.Conversations.AddRange(ashaConversation, new Conversation { CustomerId = bela.Id });
        await _db.SaveChangesAsync();

        var page = await Conversations().GetPagedAsync(new PagedRequest { Search = "sha" });

        Assert.Equal(new[] { ashaConversation.Id }, page.Items.Select(c => c.Id));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task A_conversations_message_total_is_right_on_a_short_and_a_full_page()
    {
        var customer = await AddCustomerAsync("Asha");
        var conversation = new Conversation { CustomerId = customer.Id };
        _db.Conversations.Add(conversation);
        for (var i = 0; i < 5; i++)
            _db.Messages.Add(new Message { CustomerId = customer.Id, ConversationId = conversation.Id, IdempotencyKey = $"k{i}", Text = $"m{i}" });
        await _db.SaveChangesAsync();

        var shortPage = await Conversations().GetMessagesAsync(conversation.Id, new PagedRequest { Page = 1, PageSize = 20 });
        var fullPage = await Conversations().GetMessagesAsync(conversation.Id, new PagedRequest { Page = 1, PageSize = 2 });

        Assert.Equal(5, shortPage.TotalCount);
        Assert.Equal(5, fullPage.TotalCount);
        Assert.Equal(3, fullPage.TotalPages);
    }

    [Fact]
    public async Task An_import_creates_a_new_tag_once_however_many_rows_use_it()
    {
        _import.Rows =
        [
            new CustomerImportRow(2, "9820000001", "Asha", null, null, ["Diwali", "VIP"]),
            new CustomerImportRow(3, "9820000002", "Bela", null, null, ["diwali "]),
        ];
        _db.CustomerTags.Add(new CustomerTag { Name = "VIP" });
        await _db.SaveChangesAsync();

        var result = await Customers().ImportAsync(Stream.Null, "c.csv");

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(new[] { "Diwali", "VIP" }, await _db.CustomerTags.Select(t => t.Name).OrderBy(n => n).ToListAsync());
        var tagged = await _db.Customers.Include(c => c.Tags).Where(c => c.Tags.Any(t => t.Name == "Diwali")).CountAsync();
        Assert.Equal(2, tagged);
    }

    [Fact]
    public async Task An_import_row_that_is_skipped_creates_no_tag()
    {
        var existing = await AddCustomerAsync("Asha");
        _import.Rows = [new CustomerImportRow(2, existing.PhoneNumberE164, "Asha", null, null, ["Orphan"])];

        var result = await Customers().ImportAsync(Stream.Null, "c.csv");

        Assert.Equal(1, result.SkippedDuplicateCount);
        Assert.Empty(await _db.CustomerTags.ToListAsync());
    }

    [Fact]
    public async Task An_import_skips_this_tenants_deleted_numbers_but_not_another_tenants()
    {
        var deleted = await AddCustomerAsync("Gone");
        deleted.IsDeleted = true;
        _db.Customers.Add(new Customer { TenantId = Guid.NewGuid(), PhoneNumberE164 = "+919820000009", FirstName = "Elsewhere" });
        await _db.SaveChangesAsync();
        _import.Rows =
        [
            new CustomerImportRow(2, deleted.PhoneNumberE164, "Gone", null, null, []),
            new CustomerImportRow(3, "9820000009", "Mine", null, null, []),
        ];

        var result = await Customers().ImportAsync(Stream.Null, "c.csv");

        Assert.Equal(1, result.SkippedDuplicateCount);
        Assert.Equal(1, result.ImportedCount);
        Assert.True(await _db.Customers.AnyAsync(c => c.PhoneNumberE164 == "+919820000009" && c.FirstName == "Mine"));
    }

    private sealed class FakeImport : ICustomerImportService
    {
        public IReadOnlyList<CustomerImportRow> Rows { get; set; } = [];

        public Task<IReadOnlyList<CustomerImportRow>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
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
