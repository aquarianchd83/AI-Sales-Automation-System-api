using Microsoft.EntityFrameworkCore;
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

        // SQLite never generates a rowversion, so the wallet's concurrency token is stored as supplied.
        // That means these tests cover the ledger rules, not the optimistic-concurrency race itself,
        // which only SQL Server can exercise.
        builder.Entity<WhatsAppSalesAutomation.Domain.Entities.Billing.QuotaWallet>()
            .Property(w => w.RowVersion).ValueGeneratedNever().IsConcurrencyToken(false);
        builder.Entity<WhatsAppSalesAutomation.Domain.Entities.Campaigns.CampaignCustomer>()
            .Property(c => c.RowVersion).ValueGeneratedNever().IsConcurrencyToken(false);

        foreach (var property in builder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.GetColumnType() is { } columnType && columnType.StartsWith("nvarchar(max)", StringComparison.OrdinalIgnoreCase))
                property.SetColumnType("TEXT");
        }
    }
}
