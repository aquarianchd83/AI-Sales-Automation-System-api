using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Customers;

/// <summary>
/// The free-text customer match shared by the customers, inbox and leads lists: a substring of the phone number, first name or
/// last name (plus email on the customers list). It stays a LIKE '%term%', so it cannot seek, but every column it reads is
/// carried by the narrow IX_Customers_TenantId_CreatedAt index (see CustomerConfiguration), so the scan is over that index's
/// small rows for one tenant rather than over the wide customer rows.
/// </summary>
public static class CustomerSearch
{
    public static IQueryable<Customer> Matching(IQueryable<Customer> customers, string search, bool includeEmail = false)
    {
        var term = search.Trim();

        // Two literal predicates rather than one with an "includeEmail" flag, so the flag never becomes a SQL parameter.
        return includeEmail
            ? customers.Where(c =>
                c.PhoneNumberE164.Contains(term) ||
                (c.FirstName != null && c.FirstName.Contains(term)) ||
                (c.LastName != null && c.LastName.Contains(term)) ||
                (c.Email != null && c.Email.Contains(term)))
            : customers.Where(c =>
                c.PhoneNumberE164.Contains(term) ||
                (c.FirstName != null && c.FirstName.Contains(term)) ||
                (c.LastName != null && c.LastName.Contains(term)));
    }
}
