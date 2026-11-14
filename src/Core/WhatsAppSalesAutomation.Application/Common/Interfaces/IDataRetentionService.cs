namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Rows removed in one pass, by kind of data. A kind that is switched off (0 days) is absent.</summary>
public record DataRetentionResult(IReadOnlyDictionary<string, int> Deleted)
{
    public int Total => Deleted.Values.Sum();
}

/// <summary>Deletes what has outlived its retention period (see <c>RetentionOptions</c>). Idempotent: running it twice removes nothing the second time.</summary>
public interface IDataRetentionService
{
    Task<DataRetentionResult> RunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Frees space when the database is nearly full: erases old rows from exactly the <paramref name="tables"/> the product owner selected (keys from
    /// <c>CleanableTableCatalog</c>; unknown keys are ignored), keeping <paramref name="keepDays"/> days of each - or more, when the table has a
    /// longer floor in the catalog (audit log 90 days, customer messages 180) - and never fewer than <c>RetentionOptions.MinimumDays</c>.
    /// Only ever shortens the regular retention. Only the catalog's tables can be touched; customers, payments and billing are not in it.
    /// </summary>
    Task<DataRetentionResult> RunEarlyAsync(int keepDays, IReadOnlyCollection<string> tables, CancellationToken cancellationToken = default);

    /// <summary>How many rows an early clean-up keeping <paramref name="keepDays"/> days would erase from each catalog table right now, without
    /// deleting anything. Counts with the very query <see cref="RunEarlyAsync"/> deletes by.</summary>
    Task<IReadOnlyList<CleanupEstimate>> EstimateEarlyAsync(int keepDays, CancellationToken cancellationToken = default);
}

/// <param name="TableName">Schema-qualified table name, as the database reports it (e.g. "dbo.WebhookEvents").</param>
/// <param name="EligibleRows">Rows older than <paramref name="KeepDays"/> days that the clean-up would erase.</param>
/// <param name="KeepDays">The days actually kept for this table (the owner's number, raised to the table's own floor).</param>
public record CleanupEstimate(string Key, string TableName, long EligibleRows, int KeepDays);
