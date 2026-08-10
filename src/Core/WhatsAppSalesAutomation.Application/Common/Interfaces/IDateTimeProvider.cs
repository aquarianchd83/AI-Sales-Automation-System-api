namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Testable indirection over <see cref="DateTime.UtcNow"/>.</summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
