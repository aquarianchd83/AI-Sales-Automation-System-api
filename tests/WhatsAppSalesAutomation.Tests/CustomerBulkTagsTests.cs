using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Tagging several customers at once: one request, every selected customer, tags created once and never duplicated.</summary>
public sealed class CustomerBulkTagsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly CustomerService _service;
    private readonly Guid _tenant = Guid.NewGuid();
    private int _phoneSeed;

    public CustomerBulkTagsTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        _service = new CustomerService(
            _db, null!, new TestClock(),
            new CreateCustomerRequestValidator(), new UpdateCustomerRequestValidator(),
            new BulkDeleteCustomersRequestValidator(), new OptInCustomerRequestValidator(),
            new BulkAddCustomerTagsRequestValidator());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> AddCustomerAsync(string name, params string[] tags)
    {
        var customer = new Customer { TenantId = _tenant, PhoneNumberE164 = "+9190000" + (++_phoneSeed).ToString("D5"), FirstName = name };
        foreach (var tagName in tags)
        {
            var tag = await _db.CustomerTags.FirstOrDefaultAsync(t => t.Name == tagName) ?? new CustomerTag { TenantId = _tenant, Name = tagName };
            customer.Tags.Add(tag);
        }

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<List<string>> TagsOfAsync(Guid id) =>
        (await _db.Customers.Include(c => c.Tags).SingleAsync(c => c.Id == id)).Tags.Select(t => t.Name).OrderBy(n => n).ToList();

    [Fact]
    public async Task Every_selected_customer_gets_every_tag_and_each_tag_is_created_once()
    {
        var a = await AddCustomerAsync("Asha");
        var b = await AddCustomerAsync("Bela");
        var c = await AddCustomerAsync("Chitra");

        var result = await _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(new[] { a, b }, new[] { "VIP", "October" }));

        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(2, result.UpdatedCount);
        Assert.Empty(result.NotFoundIds);
        Assert.Equal(new[] { "October", "VIP" }, await TagsOfAsync(a));
        Assert.Equal(new[] { "October", "VIP" }, await TagsOfAsync(b));
        Assert.Empty(await TagsOfAsync(c));
        Assert.Equal(2, await _db.CustomerTags.CountAsync());
    }

    [Fact]
    public async Task An_existing_tag_is_reused_matched_case_insensitively_and_not_duplicated_on_a_customer()
    {
        var a = await AddCustomerAsync("Asha", "VIP");
        var b = await AddCustomerAsync("Bela");

        var result = await _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(new[] { a, b }, new[] { "vip" }));

        Assert.Equal(1, result.UpdatedCount); // Asha already had it
        Assert.Equal(new[] { "VIP" }, await TagsOfAsync(a));
        Assert.Equal(new[] { "VIP" }, await TagsOfAsync(b));
        Assert.Equal(1, await _db.CustomerTags.CountAsync());
    }

    [Fact]
    public async Task Unknown_deleted_and_repeated_ids_are_reported_not_fatal()
    {
        var a = await AddCustomerAsync("Asha");
        var gone = await AddCustomerAsync("Gone");
        var goneCustomer = await _db.Customers.SingleAsync(c => c.Id == gone);
        goneCustomer.IsDeleted = true;
        await _db.SaveChangesAsync();
        var unknown = Guid.NewGuid();

        var result = await _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(new[] { a, a, gone, unknown }, new[] { "VIP" }));

        Assert.Equal(3, result.RequestedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(new[] { gone, unknown }.OrderBy(i => i), result.NotFoundIds.OrderBy(i => i));
        Assert.Equal(new[] { "VIP" }, await TagsOfAsync(a));
    }

    [Fact]
    public async Task Tag_names_are_trimmed_and_blank_or_repeated_ones_ignored()
    {
        var a = await AddCustomerAsync("Asha");

        var result = await _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(new[] { a }, new[] { "  VIP ", "", "vip", "   " }));

        Assert.Equal(new[] { "VIP" }, result.TagNames);
        Assert.Equal(new[] { "VIP" }, await TagsOfAsync(a));
    }

    [Fact]
    public async Task Another_tenants_customer_is_never_touched()
    {
        var mine = await AddCustomerAsync("Mine");
        var other = new Customer { TenantId = Guid.NewGuid(), PhoneNumberE164 = "+919111111111", FirstName = "Other" };
        _db.Customers.Add(other);
        await _db.SaveChangesAsync();

        var result = await _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(new[] { mine, other.Id }, new[] { "VIP" }));

        Assert.Equal(new[] { other.Id }, result.NotFoundIds);
        Assert.Empty(await _db.Customers.IgnoreQueryFilters().Where(c => c.Id == other.Id).SelectMany(c => c.Tags).ToListAsync());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Both_a_customer_and_a_tag_are_required(int idCount, int tagCount)
    {
        var request = new BulkAddCustomerTagsRequest(
            Enumerable.Range(0, idCount).Select(_ => Guid.NewGuid()).ToList(),
            Enumerable.Range(0, tagCount).Select(i => $"t{i}").ToList());

        await Assert.ThrowsAsync<ValidationException>(() => _service.BulkAddTagsAsync(request));
    }

    [Fact]
    public async Task Over_long_tags_and_oversized_batches_are_refused()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(new[] { Guid.NewGuid() }, new[] { new string('x', 101) })));

        var tooMany = Enumerable.Range(0, BulkDeleteCustomersRequestValidator.MaxIds + 1).Select(_ => Guid.NewGuid()).ToList();
        await Assert.ThrowsAsync<ValidationException>(() => _service.BulkAddTagsAsync(new BulkAddCustomerTagsRequest(tooMany, new[] { "VIP" })));
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
