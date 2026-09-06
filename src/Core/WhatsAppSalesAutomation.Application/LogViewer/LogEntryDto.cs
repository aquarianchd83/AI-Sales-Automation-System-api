namespace WhatsAppSalesAutomation.Application.LogViewer;

/// <summary>
/// <paramref name="Level"/> is Serilog's full level name (e.g. "Warning"), normalized from whichever
/// form the file uses (the output template writes the 3-letter code, "WRN"). <paramref name="Module"/>
/// is the fully qualified class that logged it (Serilog's "Namespace" property, from the
/// Serilog.Enrichers.CallerInfo stack-trace enricher - see Program.cs) and <paramref name="Method"/> is
/// the method that called it; both are null for lines written before that enricher was added, and for
/// events whose call frame never entered our own assemblies (e.g. some ASP.NET Core/EF Core/Hangfire
/// internal logging).
/// </summary>
public record LogEntryDto(DateTimeOffset Timestamp, string Level, string? Module, string? Method, string Message);
