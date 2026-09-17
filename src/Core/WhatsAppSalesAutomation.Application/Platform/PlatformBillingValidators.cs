using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Platform;

public class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    public CreatePlanRequestValidator(IApplicationDbContext context)
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(40)
            .Matches("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")
            .WithMessage("Code must be lowercase letters, digits and hyphens only, e.g. \"starter\".")
            .MustAsync(async (code, cancellationToken) =>
                !await context.Plans.AnyAsync(p => p.Code == code, cancellationToken))
            .WithMessage("A plan with this code already exists.");

        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxUsers).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxMessagesPerMonth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxCampaigns).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxKnowledgeBaseArticles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxLeadDiscoveryBatchSize).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceMonthlyCents).GreaterThanOrEqualTo(0);
    }
}

public class UpdatePlanRequestValidator : AbstractValidator<UpdatePlanRequest>
{
    public UpdatePlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxUsers).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxMessagesPerMonth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxCampaigns).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxKnowledgeBaseArticles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxLeadDiscoveryBatchSize).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceMonthlyCents).GreaterThanOrEqualTo(0);
    }
}
