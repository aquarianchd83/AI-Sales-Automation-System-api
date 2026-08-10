using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// The Application layer's view of the database. Services depend on this instead of a
/// generic repository/unit-of-work pair - EF Core's DbContext already <i>is</i> both,
/// so wrapping it further would just be ceremony. Identity's own Users/Roles DbSets are
/// reached through <see cref="Microsoft.AspNetCore.Identity.UserManager{TUser}"/> /
/// <see cref="Microsoft.AspNetCore.Identity.RoleManager{TRole}"/> instead of here.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Customer> Customers { get; }

    DbSet<CustomerTag> CustomerTags { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
