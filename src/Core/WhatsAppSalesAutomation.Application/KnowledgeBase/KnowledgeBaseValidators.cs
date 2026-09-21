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
        // Any defined source type parses here; whether the CALLER may author it is a separate,
        // tenancy-dependent question that KnowledgeBaseService.CreateAsync answers - a validator has
        // no tenant context, and a rule that silently depended on one would be the wrong kind of
        // subtle. See KnowledgeAuthority.GlobalOnly.
        RuleFor(x => x.SourceType)
            .Must(s => Enum.TryParse<KnowledgeSourceType>(s, ignoreCase: true, out _))
            .WithMessage($"SourceType must be one of: {string.Join(", ", Enum.GetNames<KnowledgeSourceType>())}.");
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
