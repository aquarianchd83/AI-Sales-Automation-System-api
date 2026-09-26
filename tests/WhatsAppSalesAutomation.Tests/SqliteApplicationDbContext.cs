using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The real model, made to run on in-memory SQLite: the production configurations pin SQL Server column
/// types (nvarchar(max)) SQLite can't create, and SQLite can't compare or sum decimals, so decimals are
/// stored as doubles here. Only the storage mapping changes - every entity, key, index and relationship
/// the ledger relies on is the production one.
/// </summary>
public sealed class SqliteApplicationDbContext : ApplicationDbContext
{
    /// <summary>What the production TenantStamping interceptor does for a tenant request: an inserted
    /// tenant-owned row with no tenant gets the current one.</summary>
    public Guid? StampTenantId { get; set; }

    /// <summary>Runs before every save - lets a test fail a specific write, or change the world mid-run.</summary>
    public Action<SqliteApplicationDbContext>? BeforeSave { get; set; }

    public SqliteApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options, ITenantContext tenantContext, ICurrentUserService currentUser)
        : base(options, tenantContext, currentUser)
    {
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        BeforeSave?.Invoke(this);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Stamp()
    {
        if (StampTenantId is not { } tenantId)
            return;

        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State == EntityState.Added && e.Entity.TenantId == Guid.Empty))
            entry.Entity.TenantId = tenantId;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<decimal>().HaveConversion<double>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        foreach (var property in builder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.GetColumnType() is { } columnType && columnType.StartsWith("nvarchar(max)", StringComparison.OrdinalIgnoreCase))
                property.SetColumnType("TEXT");

            // SQLite never generates a rowversion, so every concurrency token is stored as supplied.
            // That means these tests cover the entity rules, not the optimistic-concurrency race
            // itself, which only SQL Server can exercise.
            //
            // Applied by walking the model rather than naming entities one at a time. The hand-listed
            // version covered QuotaWallet and CampaignCustomer, and then Lead.RowVersion arrived in
            // Phase 7 and was never added here - which failed every test that inserts a Lead with
            // "NOT NULL constraint failed: Leads.RowVersion", a message pointing at the test harness
            // rather than at the entity that actually changed. A loop cannot fall behind.
            if (property.IsConcurrencyToken && property.ClrType == typeof(byte[]))
            {
                property.ValueGenerated = ValueGenerated.Never;
                property.IsConcurrencyToken = false;
            }
        }
    }
}
