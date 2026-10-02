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
}
