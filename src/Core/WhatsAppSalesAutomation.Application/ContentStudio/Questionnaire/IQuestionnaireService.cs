namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>
/// The tenant's questionnaire: which one it is shown, its answers so far, saving them and completing it. Every method refuses with
/// <c>FeatureDisabledException</c> unless the platform has the studio switched on for this tenant. Explicit tenant ids, as the other
/// studio services - the caller is a request or a job that already knows its tenant.
/// </summary>
public interface IQuestionnaireService
{
    /// <summary>Whether the studio is switched on for this tenant - what the sidenav asks, so a link only shows for a screen that will open.
    /// Never throws for a tenant that has it off.</summary>
    Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The questionnaire for this tenant, started (and pre-filled from the business profile) the first time it is asked for.</summary>
    Task<QuestionnaireStateDto> GetStateAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<QuestionnaireStateDto> SaveAnswersAsync(Guid tenantId, SaveQuestionnaireAnswersRequest request, CancellationToken cancellationToken = default);

    /// <summary>Checks every asked, required question is answered, then completes it and turns the answers into draft business facts.</summary>
    Task<QuestionnaireCompletionDto> CompleteAsync(Guid tenantId, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Starts a fresh round on the newest published version, carrying over every answer that still fits. How a tenant changes
    /// answers after completing, and how it moves to a newer version.</summary>
    Task<QuestionnaireStateDto> RestartAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public interface IBusinessFactService
{
    Task<Common.Models.PagedResult<BusinessFactDto>> ListAsync(Guid tenantId, BusinessFactQuery query, CancellationToken cancellationToken = default);

    Task<BusinessFactSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<BusinessFactDto> CreateAsync(Guid tenantId, Guid? actorUserId, SaveBusinessFactRequest request, CancellationToken cancellationToken = default);

    /// <summary>Edits a fact the tenant wrote itself. A fact from the questionnaire is changed by answering the question again.
    /// Changing a verified fact's wording sends it back to Draft: it has to be confirmed again.</summary>
    Task<BusinessFactDto> UpdateAsync(Guid tenantId, Guid id, Guid? actorUserId, SaveBusinessFactRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records that <paramref name="actorUserId"/> confirmed the fact is true. Who may call this is decided by the endpoint.</summary>
    Task<BusinessFactDto> VerifyAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<BusinessFactDto> RetireAsync(Guid tenantId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>The platform operator's side: the templates every tenant is asked, their versions, and publishing.</summary>
public interface IQuestionnaireTemplateService
{
    Task<IReadOnlyList<QuestionnaireTemplateSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<QuestionnaireTemplateDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates the template with its first version, as a draft.</summary>
    Task<QuestionnaireTemplateDetailDto> CreateAsync(CreateQuestionnaireTemplateRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<QuestionnaireTemplateDetailDto> UpdateAsync(Guid id, UpdateQuestionnaireTemplateRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Starts a new draft version, copied from the newest version. Refused while a draft already exists.</summary>
    Task<QuestionnaireTemplateDetailDto> CreateVersionAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Replaces a draft version's definition. A published version cannot be edited.</summary>
    Task<QuestionnaireTemplateDetailDto> SaveVersionAsync(Guid id, int number, SaveQuestionnaireVersionRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Checks the definition and, if sound, publishes it: tenants are offered it from now on. Irreversible.</summary>
    Task<QuestionnaireTemplateDetailDto> PublishVersionAsync(Guid id, int number, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    QuestionnaireValidationDto Validate(string definitionJson);

    string GetStarterDefinition();
}
