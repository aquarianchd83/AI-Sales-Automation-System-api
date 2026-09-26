namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Identifies this running application instance - e.g. as the owner of a distributed lock. Stable for
/// the life of the process, and distinct across machines, processes and restarts.</summary>
public interface IApplicationInstance
{
    string Id { get; }
}

public sealed class ApplicationInstance : IApplicationInstance
{
    public string Id { get; } = $"{Truncate(Environment.MachineName, 100)}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    private static string Truncate(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;
}
