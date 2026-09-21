using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;

/// <summary>An entity type that is audited, and the ONLY properties of it that are recorded.</summary>
public sealed record AuditedEntity(string Name, IReadOnlyList<string> Properties);

/// <summary>
/// What is audited, and which properties of it are recorded.
///
/// This is an allow-list on both axes, on purpose. The design document imagines a SaveChanges hook
/// capturing every entity change; done literally, that writes message bodies, customers' phone
/// numbers, stored credentials and password hashes into a table that Admins can read and that is
/// deliberately never edited or purged. The failure it would create is not a bug anyone notices - it is
/// a permanent, growing copy of everything sensitive, in a place nobody was thinking about when they
/// wrote the schema.
///
/// So a type appears here only when someone has decided what about it is worth accounting for, and
/// lists exactly those properties. Adding a new audited property is one line; the default for
/// everything else is that it is not recorded.
/// </summary>
public static class AuditedEntityCatalog
{
    /// <summary>Names of properties whose change makes an Update a StatusChange.</summary>
    private static readonly HashSet<string> LifecycleProperties = new(StringComparer.Ordinal) { "Status", "Stage", "Mode" };

    private static readonly IReadOnlyDictionary<Type, AuditedEntity> Entities = new Dictionary<Type, AuditedEntity>
    {
        [typeof(Lead)] = new("Lead", new[] { "Stage", "Score", "AssignedTo", "HotLeadDetectedAt", "CampaignId" }),
        [typeof(Campaign)] = new("Campaign", new[] { "Name", "Status", "ScheduledStartAt", "StartedAt", "StoppedAt" }),
        [typeof(Conversation)] = new("Conversation", new[] { "Mode", "Status", "AssignedAgentId" }),
        [typeof(HumanHandoff)] = new("Handoff", new[] { "Status", "AssignedAgentId", "TriggerReason", "ResolvedAt" }),

        // Opt-in state is a compliance record, so it is audited. The phone number and name are not.
        [typeof(Customer)] = new("Customer", new[] { "OptInStatus", "OptOutSource" }),

        // Never Content: an article's body can be large and is versioned in its own table.
        [typeof(KnowledgeBaseArticle)] = new("KnowledgeArticle", new[] { "Title", "Status", "SourceType", "AuthorityRank", "VersionNumber", "IsCurrentVersion" })
    };

    public static bool TryGet(Type clrType, out AuditedEntity entity) => Entities.TryGetValue(clrType, out entity!);

    public static bool IsLifecycle(string property) => LifecycleProperties.Contains(property);
}

/// <summary>
/// Writes an <see cref="AuditLog"/> row alongside every change to an audited entity, in the same save -
/// so the change and its record commit together or not at all, and there is no window in which one
/// exists without the other.
///
/// Registered FIRST among the interceptors, and that ordering is load-bearing: the audit rows are added
/// to the change tracker during SavingChanges, and the timestamp and tenant-stamping interceptors that
/// run after it then treat them like any other new row.
///
/// Also the enforcement point for append-only: a modified or deleted AuditLog fails the save.
/// </summary>
public sealed class AuditTrailSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;

    public AuditTrailSaveChangesInterceptor(ICurrentUserService currentUser, IDateTimeProvider clock)
    {
        _currentUser = currentUser;
        _clock = clock;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
            return;

        var entries = context.ChangeTracker.Entries().ToList();

        foreach (var entry in entries.Where(e => e.Entity is AuditLog && e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Audit log entries are append-only and cannot be modified or deleted.");
        }

        var rows = new List<AuditLog>();

        foreach (var entry in entries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            if (!AuditedEntityCatalog.TryGet(entry.Entity.GetType(), out var audited))
                continue;

            var row = Build(entry, audited);
            if (row is not null)
                rows.Add(row);
        }

        if (rows.Count > 0)
            context.AddRange(rows);
    }

    private AuditLog? Build(EntityEntry entry, AuditedEntity audited)
    {
        // A GLOBAL (platform-owned) row has no tenant to show a trail to; the platform's own audit
        // log records what its staff do to it.
        var tenantId = TenantOf(entry);
        if (tenantId is null || tenantId == Guid.Empty)
            return null;

        var entityId = entry.Property("Id").CurrentValue is Guid id ? id : Guid.Empty;
        AuditAction action;
        var changes = new Dictionary<string, object?>();

        switch (entry.State)
        {
            case EntityState.Added:
                action = AuditAction.Create;
                foreach (var name in audited.Properties)
                    changes[name] = Format(entry.Property(name).CurrentValue);
                break;

            case EntityState.Deleted:
                action = AuditAction.Delete;
                break;

            default:
                // Soft delete arrives as a modification of IsDeleted, and is a delete to a reader.
                if (entry.Entity is ISoftDelete && entry.Property(nameof(ISoftDelete.IsDeleted)) is { } softDelete
                    && softDelete.IsModified && softDelete.CurrentValue is true)
                {
                    action = AuditAction.Delete;
                    break;
                }

                action = AuditAction.Update;
                foreach (var name in audited.Properties)
                {
                    var property = entry.Property(name);
                    if (!property.IsModified || Equals(property.OriginalValue, property.CurrentValue))
                        continue;

                    changes[name] = new Dictionary<string, object?>
                    {
                        ["from"] = Format(property.OriginalValue),
                        ["to"] = Format(property.CurrentValue)
                    };

                    if (AuditedEntityCatalog.IsLifecycle(name))
                        action = AuditAction.StatusChange;
                }

                // Only non-audited properties changed (a timestamp, a counter). Nothing to account for.
                if (changes.Count == 0)
                    return null;

                break;
        }

        return new AuditLog
        {
            TenantId = tenantId.Value,
            EntityName = audited.Name,
            EntityId = entityId,
            Action = action,
            ChangesJson = JsonSerializer.Serialize(changes),
            PerformedBy = _currentUser.UserId,
            ImpersonatedBy = _currentUser.ImpersonatorUserId,
            PerformedAt = _clock.UtcNow,
            CreatedAt = _clock.UtcNow,
            IpAddress = _currentUser.IpAddress
        };
    }

    /// <summary>Set explicitly rather than left to the tenant-stamping interceptor, which has already
    /// looked for new rows by the time these are added - and which would stamp them with the CURRENT
    /// tenant, not the tenant the audited entity actually belongs to.</summary>
    private static Guid? TenantOf(EntityEntry entry) => entry.Entity switch
    {
        ITenantOwned owned => owned.TenantId,
        ITenantScopedOrGlobal scoped => scoped.TenantId,
        _ => null
    };

    /// <summary>Enums as names and dates in a round-trip format, so the JSON reads the same to a person
    /// as to a program and does not shift meaning when an enum is renumbered.</summary>
    private static object? Format(object? value) => value switch
    {
        null => null,
        Enum e => e.ToString(),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        _ => value
    };
}
