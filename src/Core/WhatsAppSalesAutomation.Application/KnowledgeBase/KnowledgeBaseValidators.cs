using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public class CreateKnowledgeBaseArticleRequestValidator : AbstractValidator<CreateKnowledgeBaseArticleRequest>
{
    public CreateKnowledgeBaseArticleRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).MaximumLength(100);
        RuleFor(x => x.Content).NotEmpty();
        RuleFor(x => x.SourceType)
            .Must(s => Enum.TryParse<KnowledgeBaseSourceType>(s, ignoreCase: true, out _))
            .WithMessage($"SourceType must be one of: {string.Join(", ", Enum.GetNames<KnowledgeBaseSourceType>())}.");
    }
}

public class UpdateKnowledgeBaseArticleRequestValidator : AbstractValidator<UpdateKnowledgeBaseArticleRequest>
{
    public UpdateKnowledgeBaseArticleRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).MaximumLength(100);
        RuleFor(x => x.Content).NotEmpty();
    }
}
