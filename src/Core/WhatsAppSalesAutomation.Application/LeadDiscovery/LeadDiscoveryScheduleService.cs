using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>One date and time the tenant has set, in the tenant's own time zone ("2026-11-26", "10:30").</summary>
public record LeadDiscoveryScheduleSlotDto(Guid Id, string Date, string Time, bool RepeatDaily, bool IsDone, DateTime NextRunAtUtc, DateTime? LastRunAtUtc);

/// <summary>A run the schedule will start, for the "Coming up" list. <paramref name="Slot"/> is the slot's place in the list, from 1.</summary>
public record LeadDiscoveryUpcomingRunDto(DateTime RunAtUtc, int Slot, bool RepeatDaily);

public record LeadDiscoveryScheduleDto(
    string TimeZoneId, int MaxSlots, IReadOnlyList<LeadDiscoveryScheduleSlotDto> Slots, IReadOnlyList<LeadDiscoveryUpcomingRunDto> Upcoming, DateTime? NextRunAtUtc);

/// <summary>A slot as the tenant set it. <paramref name="Id"/> is null for a new one; a slot left out of the request is removed.</summary>
public record SaveLeadDiscoverySlot(Guid? Id, string Date, string Time, bool RepeatDaily);

public record SaveLeadDiscoveryScheduleRequest(IReadOnlyList<SaveLeadDiscoverySlot> Slots);

public interface ILeadDiscoveryScheduleService
{
    Task<LeadDiscoveryScheduleDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the tenant's slots with these. Refused (400) for more than five that are not done, a date and time that is not in the future, or two the same.</summary>
    Task<LeadDiscoveryScheduleDto> SaveAsync(Guid tenantId, SaveLeadDiscoveryScheduleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every tenant with a slot that is due now, once each: the due slots are moved on (a repeating one to its next day, a single one to done) before this returns,
    /// so the same time can never start two runs. Not tenant-scoped - this is what the minute-by-minute job asks.</summary>
    Task<IReadOnlyList<Guid>> ClaimDueAsync(CancellationToken cancellationToken = default);
}

public class LeadDiscoveryScheduleService : ILeadDiscoveryScheduleService
{
    public const int MaxSlots = 5;
    private const int UpcomingShown = 3;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public LeadDiscoveryScheduleService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<LeadDiscoveryScheduleDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var slots = await LoadAsync(tenantId, cancellationToken);
        return ToDto(slots, zone);
    }

    public async Task<LeadDiscoveryScheduleDto> SaveAsync(Guid tenantId, SaveLeadDiscoveryScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var existing = await LoadAsync(tenantId, cancellationToken);
        var now = _dateTime.UtcNow;
        var requested = request.Slots ?? Array.Empty<SaveLeadDiscoverySlot>();

        var kept = new List<LeadDiscoveryScheduleSlot>();
        var active = new List<(DateTime RunAtUtc, LeadDiscoveryScheduleSlot Slot)>();
        var errors = new List<ValidationFailure>();

        foreach (var item in requested)
        {
            var current = item.Id is { } id ? existing.FirstOrDefault(s => s.Id == id) : null;

            // A slot that has already run once is only kept or removed, never edited.
            if (current is { IsDone: true })
            {
                kept.Add(current);
                continue;
            }

            if (!TryParseLocal(item.Date, item.Time, out var local))
            {
                errors.Add(new ValidationFailure("slots", "Pick a date and a time for every slot."));
                continue;
            }

            var runAtUtc = ToUtc(local, zone);

            // Untouched slots may be due within the minute; only a date and time the tenant has just set must be ahead of now.
            var unchanged = current is not null && current.NextRunAtUtc == runAtUtc && current.RepeatDaily == item.RepeatDaily;
            if (!unchanged && runAtUtc <= now)
            {
                errors.Add(new ValidationFailure("slots", "Pick a date and time in the future."));
                continue;
            }

            var slot = current ?? new LeadDiscoveryScheduleSlot { TenantId = tenantId };
            slot.NextRunAtUtc = runAtUtc;
            slot.RepeatDaily = item.RepeatDaily;
            active.Add((runAtUtc, slot));
            kept.Add(slot);
        }

        if (active.Count > MaxSlots)
            errors.Add(new ValidationFailure("slots", $"You can set up to {MaxSlots} times."));
        if (active.GroupBy(a => a.RunAtUtc).Any(g => g.Count() > 1))
            errors.Add(new ValidationFailure("slots", "Two of the times are the same."));
        if (errors.Count > 0)
            throw new ValidationException(errors.DistinctBy(e => e.ErrorMessage).ToList());

        foreach (var gone in existing.Where(e => kept.All(k => k.Id != e.Id)))
            _context.LeadDiscoveryScheduleSlots.Remove(gone);
        foreach (var slot in kept.Where(k => existing.All(e => e.Id != k.Id)))
            _context.LeadDiscoveryScheduleSlots.Add(slot);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(await LoadAsync(tenantId, cancellationToken), zone);
    }

    public async Task<IReadOnlyList<Guid>> ClaimDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var due = await _context.LeadDiscoveryScheduleSlots.IgnoreQueryFilters()
            .Where(s => !s.IsDone && s.NextRunAtUtc <= now)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
            return Array.Empty<Guid>();

        var tenantIds = due.Select(s => s.TenantId).Distinct().ToList();
        var tenants = await _context.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Status, t.Timezone })
            .ToListAsync(cancellationToken);

        var runs = new List<Guid>();
        foreach (var slot in due)
        {
            var tenant = tenants.FirstOrDefault(t => t.Id == slot.TenantId);
            if (tenant is null)
                continue;

            slot.LastRunAtUtc = now;
            if (slot.RepeatDaily)
                slot.NextRunAtUtc = NextDay(slot.NextRunAtUtc, now, TimeZoneCatalog.Resolve(tenant.Timezone));
            else
                slot.IsDone = true;

            // A suspended or cancelled tenant's time passes without a run, the way its other background jobs stop.
            if (TenantStatusRules.AllowsBackgroundJobs(tenant.Status) && !runs.Contains(tenant.Id))
                runs.Add(tenant.Id);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return runs;
    }

    // ------------------------------------------------------------------------------------------------

    private async Task<List<LeadDiscoveryScheduleSlot>> LoadAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _context.LeadDiscoveryScheduleSlots
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.NextRunAtUtc)
            .ToListAsync(cancellationToken);

    private async Task<TimeZoneInfo> ZoneAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var stored = await _context.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Timezone).FirstOrDefaultAsync(cancellationToken);
        return TimeZoneCatalog.Resolve(stored);
    }

    private LeadDiscoveryScheduleDto ToDto(IReadOnlyList<LeadDiscoveryScheduleSlot> slots, TimeZoneInfo zone)
    {
        var dtos = slots.Select(s =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s.IsDone ? s.LastRunAtUtc ?? s.NextRunAtUtc : s.NextRunAtUtc, DateTimeKind.Utc), zone);
            return new LeadDiscoveryScheduleSlotDto(
                s.Id, local.ToString("yyyy-MM-dd"), local.ToString("HH:mm"), s.RepeatDaily, s.IsDone,
                DateTime.SpecifyKind(s.NextRunAtUtc, DateTimeKind.Utc),
                s.LastRunAtUtc is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null);
        }).ToList();

        var upcoming = new List<LeadDiscoveryUpcomingRunDto>();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.IsDone)
                continue;
            var at = slot.NextRunAtUtc;
            for (var n = 0; n < (slot.RepeatDaily ? UpcomingShown : 1); n++)
            {
                upcoming.Add(new LeadDiscoveryUpcomingRunDto(DateTime.SpecifyKind(at, DateTimeKind.Utc), i + 1, slot.RepeatDaily));
                at = NextDay(at, at, zone);
            }
        }

        var shown = upcoming.OrderBy(u => u.RunAtUtc).Take(UpcomingShown).ToList();
        return new LeadDiscoveryScheduleDto(zone.Id, MaxSlots, dtos, shown, shown.Count > 0 ? shown[0].RunAtUtc : null);
    }

    /// <summary>The next day's run at the same local time, strictly after <paramref name="after"/>.</summary>
    private static DateTime NextDay(DateTime previousRunUtc, DateTime after, TimeZoneInfo zone)
    {
        var timeOfDay = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(previousRunUtc, DateTimeKind.Utc), zone).TimeOfDay;
        var day = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(previousRunUtc, DateTimeKind.Utc), zone).Date;
        DateTime next;
        do
        {
            day = day.AddDays(1);
            next = ToUtc(day + timeOfDay, zone);
        }
        while (next <= after);
        return next;
    }

    private static bool TryParseLocal(string? date, string? time, out DateTime local)
    {
        local = default;
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d)
            || !TimeSpan.TryParseExact(time, "hh\\:mm", null, out var t))
            return false;
        local = DateTime.SpecifyKind(d.Date + t, DateTimeKind.Unspecified);
        return true;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
