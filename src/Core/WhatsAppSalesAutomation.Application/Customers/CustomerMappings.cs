using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Customers;

public static class CustomerMappings
{
    public static CustomerDto ToDto(this Customer customer) => new(
        customer.Id,
        customer.PhoneNumberE164,
        customer.FirstName,
        customer.LastName,
        customer.Email,
        customer.Source,
        customer.OptInStatus.ToString(),
        customer.OptInTimestamp,
        customer.OptOutTimestamp,
        customer.PreferredLanguage,
        customer.AssignedAgentId,
        customer.Tags.Select(t => t.Name).ToList(),
        customer.CreatedAt);
}
