using System.Text.Json.Serialization;

namespace WhatsAppSalesAutomation.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlatformTicketPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlatformTicketStatus
{
    Open = 0,
    InProgress = 1,
    Resolved = 2
}

/// <summary>What raised the ticket, so the same problem is not raised twice while one is still open.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlatformTicketKind
{
    /// <summary>A tenant used AI while the platform is configured with a Simulated provider - answers cannot be trusted, and only the product owner can change that.</summary>
    SimulatedAiConfiguration = 0
}
