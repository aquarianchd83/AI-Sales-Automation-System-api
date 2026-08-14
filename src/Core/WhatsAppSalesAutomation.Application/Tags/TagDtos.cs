namespace WhatsAppSalesAutomation.Application.Tags;

/// <summary>
/// <paramref name="CustomerCount"/> counts only live customers - soft-deleted ones are excluded by
/// the global query filter, so the number matches what the customer list actually shows.
/// </summary>
public record TagDto(Guid Id, string Name, int CustomerCount, DateTime CreatedAt);

public record CreateTagRequest(string Name);

public record UpdateTagRequest(string Name);
