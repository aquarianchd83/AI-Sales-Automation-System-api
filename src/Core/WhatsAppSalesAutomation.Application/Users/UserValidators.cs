using FluentValidation;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Application.Users;

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        RuleFor(x => x.Roles)
            .NotEmpty()
            .Must(roles => roles.All(r => AppRoles.All.Contains(r)))
            .WithMessage($"Roles must be one of: {string.Join(", ", AppRoles.All)}");
    }
}

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
    }
}

public class AssignRolesRequestValidator : AbstractValidator<AssignRolesRequest>
{
    public AssignRolesRequestValidator()
    {
        RuleFor(x => x.Roles)
            .NotEmpty()
            .Must(roles => roles.All(r => AppRoles.All.Contains(r)))
            .WithMessage($"Roles must be one of: {string.Join(", ", AppRoles.All)}");
    }
}
