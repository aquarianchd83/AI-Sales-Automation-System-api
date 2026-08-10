using Microsoft.AspNetCore.Identity;

namespace WhatsAppSalesAutomation.Domain.Entities.Identity;

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName) : base(roleName)
    {
    }
}
