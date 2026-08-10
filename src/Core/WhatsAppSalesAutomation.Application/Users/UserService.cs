using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Users;

public class UserService : IUserService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IValidator<CreateUserRequest> _createValidator;
    private readonly IValidator<UpdateUserRequest> _updateValidator;
    private readonly IValidator<AssignRolesRequest> _assignRolesValidator;

    public UserService(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IValidator<CreateUserRequest> createValidator,
        IValidator<UpdateUserRequest> updateValidator,
        IValidator<AssignRolesRequest> assignRolesValidator)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _assignRolesValidator = assignRolesValidator;
    }

    public async Task<PagedResult<UserDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _userManager.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(u => u.FullName.Contains(search) || (u.Email != null && u.Email.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var users = await query
            .OrderBy(u => u.FullName)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        // N+1 by design for now: GetRolesAsync per user keeps this simple and correct.
        // Revisit with a single join query if admin user lists grow large.
        var dtos = new List<UserDto>(users.Count);
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            dtos.Add(user.ToDto(roles));
        }

        return new PagedResult<UserDto>(dtos, totalCount, request.Page, request.PageSize);
    }

    public async Task<UserDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), id);

        var roles = await _userManager.GetRolesAsync(user);
        return user.ToDto(roles);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            throw new ConflictException($"A user with email '{request.Email}' already exists.");

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            PhoneNumber = request.PhoneNumber,
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e => new FluentValidation.Results.ValidationFailure(nameof(request.Password), e.Description)));

        await _userManager.AddToRolesAsync(user, request.Roles);

        var roles = await _userManager.GetRolesAsync(user);
        return user.ToDto(roles);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), id);

        user.FullName = request.FullName;
        user.PhoneNumber = request.PhoneNumber;
        user.IsActive = request.IsActive;

        await _userManager.UpdateAsync(user);

        var roles = await _userManager.GetRolesAsync(user);
        return user.ToDto(roles);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), id);

        user.IsActive = false;
        await _userManager.UpdateAsync(user);
    }

    public async Task<UserDto> AssignRolesAsync(Guid id, AssignRolesRequest request, CancellationToken cancellationToken = default)
    {
        await _assignRolesValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), id);

        var currentRoles = await _userManager.GetRolesAsync(user);
        var toRemove = currentRoles.Except(request.Roles, StringComparer.OrdinalIgnoreCase).ToList();
        var toAdd = request.Roles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToList();

        if (toRemove.Count > 0)
            await _userManager.RemoveFromRolesAsync(user, toRemove);
        if (toAdd.Count > 0)
            await _userManager.AddToRolesAsync(user, toAdd);

        var roles = await _userManager.GetRolesAsync(user);
        return user.ToDto(roles);
    }

    public async Task<IReadOnlyList<string>> GetAllRolesAsync(CancellationToken cancellationToken = default) =>
        await _roleManager.Roles.Select(r => r.Name ?? string.Empty).ToListAsync(cancellationToken);
}
