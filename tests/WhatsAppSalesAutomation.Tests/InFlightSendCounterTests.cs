using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What "a send is mid-flight" means, against a real (SQLite) database - the condition that holds a provider switch.</summary>
public sealed class InFlightSendCounterTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Customer _customer = new() { PhoneNumberE164 = "+919800000001" };
    private int _n;

    public InFlightSendCounterTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new NoUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Customers.Add(_customer);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Add(MessageDirection direction, MessageStatus status, string? providerId)
    {
        _db.Messages.Add(new Message
        {
            CustomerId = _customer.Id, Direction = direction, Status = status, WhatsAppMessageId = providerId,
            IdempotencyKey = $"k{++_n}",
            // The test context has no audit interceptor, so the timestamp production stamps on save is set here.
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private InFlightSendCounter Counter(DateTime? now = null) => new(_db, new Clock(now ?? DateTime.UtcNow));

    [Fact]
    public async Task A_reserved_send_with_no_provider_id_is_in_flight()
    {
        Add(MessageDirection.Outbound, MessageStatus.Queued, null);

        Assert.Equal(1, await Counter().CountAsync());
    }

    [Fact]
    public async Task A_send_the_provider_already_answered_is_not_in_flight()
    {
        Add(MessageDirection.Outbound, MessageStatus.Sent, "wamid.1");
        Add(MessageDirection.Outbound, MessageStatus.Failed, null);

        Assert.Equal(0, await Counter().CountAsync());
    }

    [Fact]
    public async Task Inbound_messages_are_never_in_flight()
    {
        Add(MessageDirection.Inbound, MessageStatus.Queued, null);

        Assert.Equal(0, await Counter().CountAsync());
    }

    [Fact]
    public async Task A_send_stuck_for_longer_than_the_window_does_not_block_a_switch_forever()
    {
        Add(MessageDirection.Outbound, MessageStatus.Queued, null);

        Assert.Equal(0, await Counter(DateTime.UtcNow + InFlightSendCounter.Window + TimeSpan.FromMinutes(5)).CountAsync());
    }

    private sealed class Clock : IDateTimeProvider
    {
        public Clock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class Ambient : ITenantContext
    {
        private readonly Guid _id;
        public Ambient(Guid id) => _id = id;
        public Guid? TenantId => _id;
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
