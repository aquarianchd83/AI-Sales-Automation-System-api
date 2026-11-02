using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence;

/// <inheritdoc cref="ITenantDataPurger"/>
public sealed class TenantDataPurger : ITenantDataPurger
{
    private const int ForeignKeyViolation = 547;

    private readonly ApplicationDbContext _context;

    public TenantDataPurger(ApplicationDbContext context) => _context = context;

    public async Task<PurgeTenantResultDto> PurgeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // (schema, table, column) for every table that has a TenantId, read from the model. The audit log is the one
        // exception: it is the record that this deletion happened, and it must outlive the tenant.
        var pending = _context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.ClrType != typeof(PlatformAuditLogEntry) && e.GetTableName() is not null)
            .Select(e =>
            {
                var table = StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
                var column = e.FindProperty("TenantId")?.GetColumnName(table);
                return (Schema: e.GetSchema(), Table: e.GetTableName()!, Column: column);
            })
            .Where(t => t.Column is not null)
            .Distinct()
            .ToList();

        var rows = 0;
        var tables = 0;

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // The tables are not ordered by their foreign keys; instead keep sweeping, deferring any table a child still blocks,
        // until a full pass makes no progress. Child rows without a TenantId of their own cascade from their parent.
        while (pending.Count > 0)
        {
            var blocked = new List<(string? Schema, string Table, string? Column)>();
            foreach (var t in pending)
            {
                var name = t.Schema is null ? $"[{t.Table}]" : $"[{t.Schema}].[{t.Table}]";
                try
                {
                    // Names come from the EF model, never from input; the id is a parameter.
                    var deleted = await _context.Database.ExecuteSqlRawAsync(
                        $"DELETE FROM {name} WHERE [{t.Column}] = {{0}}", new object[] { tenantId }, cancellationToken);
                    if (deleted > 0)
                    {
                        rows += deleted;
                        tables++;
                    }
                }
                catch (SqlException ex) when (ex.Number == ForeignKeyViolation)
                {
                    blocked.Add(t);
                }
            }

            if (blocked.Count == pending.Count)
                throw new InvalidOperationException(
                    "Could not delete the tenant's data: " + string.Join(", ", blocked.Select(b => b.Table)) + " are still referenced by other rows.");
            pending = blocked;
        }

        var tenantRows = await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM [Tenants] WHERE [Id] = {0}", new object[] { tenantId }, cancellationToken);
        rows += tenantRows;

        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return new PurgeTenantResultDto(tables, rows);
    }
}
