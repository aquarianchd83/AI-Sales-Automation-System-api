namespace WhatsAppSalesAutomation.Infrastructure.Logging;

/// <summary>
/// Bound from the "LogViewer" config section. Defaults match the "Serilog:WriteTo" File sink's own
/// "path": "logs/log-.txt" in appsettings.json - this only reads what Serilog already writes there, so
/// if that path ever changes, this section needs to change with it.
/// </summary>
public class LocalLogFileReaderSettings
{
    /// <summary>Relative to the API's content root, or absolute.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>The file name stem before Serilog's date/sequence suffix, e.g. "log-" for
    /// "log-20260903.txt" / "log-20260903_001.txt".</summary>
    public string FilePrefix { get; set; } = "log-";
}
