using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

public class CustomerTag : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
}
