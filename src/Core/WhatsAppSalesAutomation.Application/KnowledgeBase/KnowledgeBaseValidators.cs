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

public class BulkPublishArticlesRequestValidator : AbstractValidator<BulkPublishArticlesRequest>
{
    /// <summary>Lower than BulkDeleteCustomersRequestValidator's 500 - each id here does real
    /// re-chunk/re-embed work (an external provider call per chunk), not a single UPDATE batch.</summary>
    public const int MaxIds = 100;

    public BulkPublishArticlesRequestValidator()
    {
        RuleFor(x => x.Ids)
            .NotEmpty().WithMessage("At least one article id is required.")
            .Must(ids => ids is null || ids.Count <= MaxIds)
            .WithMessage($"A maximum of {MaxIds} articles can be published per request.");
    }
}
