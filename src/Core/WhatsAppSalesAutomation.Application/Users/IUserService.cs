using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Users;

public interface IUserService
{
    Task<PagedResult<UserDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<UserDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Soft-delete: deactivates the account (IsActive = false) rather than hard-deleting.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<UserDto> AssignRolesAsync(Guid id, AssignRolesRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetAllRolesAsync(CancellationToken cancellationToken = default);
}
