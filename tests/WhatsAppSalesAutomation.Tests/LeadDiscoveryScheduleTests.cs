using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The tenant's own lead-discovery schedule: up to five dates and times in its time zone, once or daily.</summary>
public sealed class LeadDiscoveryScheduleTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new(); // 2026-09-10 12:00 UTC = 17:30 IST
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly LeadDiscoveryScheduleService _service;

    public LeadDiscoveryScheduleTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Eye", Slug = "eye", Status = TenantStatus.Active });
        _db.SaveChanges();
        _service = new LeadDiscoveryScheduleService(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveLeadDiscoverySlot Slot(string date, string time, bool daily = false, Guid? id = null) => new(id, date, time, daily);

    [Fact]
    public async Task Saved_times_come_back_in_the_tenants_time_zone_with_the_next_runs()
    {
        var saved = await _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(new[] { Slot("2026-09-11", "10:30", daily: true), Slot("2026-09-12", "18:00") }));

        Assert.Equal(new[] { "2026-09-11", "2026-09-12" }, saved.Slots.Select(s => s.Date));
        Assert.Equal(new[] { "10:30", "18:00" }, saved.Slots.Select(s => s.Time));
        // 10:30 IST is 05:00 UTC.
        Assert.Equal(new DateTime(2026, 9, 11, 5, 0, 0, DateTimeKind.Utc), saved.NextRunAtUtc);
        Assert.Equal(new[] { 1, 1, 2 }, saved.Upcoming.Select(u => u.Slot));
        Assert.Equal(new DateTime(2026, 9, 12, 5, 0, 0, DateTimeKind.Utc), saved.Upcoming[1].RunAtUtc);
    }

    [Fact]
    public async Task A_time_in_the_past_a_sixth_slot_and_two_the_same_are_refused()
    {
        var past = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(new[] { Slot("2026-09-10", "09:00") })));
        Assert.Contains("future", past.Errors.Single().ErrorMessage);

        var six = Enumerable.Range(11, 6).Select(d => Slot($"2026-09-{d}", "10:00")).ToList();
        var tooMany = await Assert.ThrowsAsync<ValidationException>(() => _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(six)));
        Assert.Contains("up to 5", tooMany.Errors.Single().ErrorMessage);

        var same = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(new[] { Slot("2026-09-11", "10:00"), Slot("2026-09-11", "10:00", daily: true) })));
        Assert.Contains("same", same.Errors.Single().ErrorMessage);
    }

    [Fact]
    public async Task A_due_single_slot_starts_one_run_and_is_done_and_a_daily_one_moves_to_tomorrow()
    {
        await _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(new[] { Slot("2026-09-11", "10:30"), Slot("2026-09-11", "11:00", daily: true) }));
        _clock.UtcNow = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc); // both are due

        var runs = await _service.ClaimDueAsync();
        var again = await _service.ClaimDueAsync();

        Assert.Equal(new[] { _tenant }, runs);
        Assert.Empty(again);
        var schedule = await _service.GetAsync(_tenant);
        var single = schedule.Slots[0];
        var daily = schedule.Slots[1];
        Assert.True(single.IsDone);
        Assert.False(daily.IsDone);
        Assert.Equal(new DateTime(2026, 9, 12, 5, 30, 0, DateTimeKind.Utc), daily.NextRunAtUtc);
        Assert.NotNull(daily.LastRunAtUtc);
    }

    [Fact]
    public async Task A_done_slot_stays_listed_and_does_not_count_toward_the_five()
    {
        await _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(new[] { Slot("2026-09-11", "10:30") }));
        _clock.UtcNow = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);
        await _service.ClaimDueAsync();
        var done = (await _service.GetAsync(_tenant)).Slots.Single();

        var request = new List<SaveLeadDiscoverySlot> { Slot(done.Date, done.Time, id: done.Id) };
        request.AddRange(Enumerable.Range(12, 5).Select(d => Slot($"2026-09-{d}", "10:00")));
        var saved = await _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(request));

        Assert.Equal(6, saved.Slots.Count);
        Assert.Single(saved.Slots, s => s.IsDone);
    }

    [Fact]
    public async Task A_suspended_tenants_slot_passes_without_a_run()
    {
        await _service.SaveAsync(_tenant, new SaveLeadDiscoveryScheduleRequest(new[] { Slot("2026-09-11", "10:30") }));
        var tenant = await _db.Tenants.SingleAsync();
        tenant.Status = TenantStatus.Suspended;
        await _db.SaveChangesAsync();
        _clock.UtcNow = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);

        Assert.Empty(await _service.ClaimDueAsync());
        Assert.True((await _service.GetAsync(_tenant)).Slots.Single().IsDone);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }
}
