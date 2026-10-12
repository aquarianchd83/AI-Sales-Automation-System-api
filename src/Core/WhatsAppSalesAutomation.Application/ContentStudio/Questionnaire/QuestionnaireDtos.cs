using System.Text.Json;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

// ----- the tenant's questionnaire --------------------------------------------------------------------------------------------------------

/// <summary>
/// Everything the wizard needs in one read: the questions, the answers so far, and which questions are asked right now (the server's
/// answer, so the screen never has to guess). <see cref="Available"/> false means no published questionnaire fits this tenant yet.
/// </summary>
public record QuestionnaireStateDto(
    bool Available,
    string? Message,
    Guid? ResponseId,
    Guid? TemplateId,
    string? TemplateTitle,
    int VersionNumber,
    QuestionnaireResponseStatus Status,
    QuestionnaireDefinition? Definition,
    IReadOnlyDictionary<string, JsonElement> Answers,
    IReadOnlyList<string> VisibleKeys,
    bool NewerVersionAvailable,
    DateTime? CompletedAtUtc);

/// <summary>Only the keys present are changed; a null or empty value removes that answer.</summary>
public record SaveQuestionnaireAnswersRequest(Dictionary<string, JsonElement> Answers);

public record QuestionnaireCompletionDto(QuestionnaireStateDto State, int FactsCreated, int FactsUpdated, int FactsRetired);

// ----- business facts --------------------------------------------------------------------------------------------------------------------

public record BusinessFactDto(
    Guid Id,
    string Category,
    string Statement,
    BusinessFactSource SourceType,
    string? SourceRef,
    BusinessFactStatus Status,
    Guid? VerifiedBy,
    string? VerifiedByName,
    DateTime? VerifiedAtUtc,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record BusinessFactQuery : PagedRequest
{
    public BusinessFactStatus? Status { get; init; }

    public string? Category { get; init; }

    public BusinessFactSource? SourceType { get; init; }
}

public record SaveBusinessFactRequest(string Category, string Statement);

public record BusinessFactSummaryDto(int Draft, int Verified, int Retired, IReadOnlyList<string> Categories);

// ----- the platform's templates ----------------------------------------------------------------------------------------------------------

public record QuestionnaireTemplateSummaryDto(
    Guid Id,
    string Key,
    string Title,
    string? Description,
    string? Industry,
    bool IsActive,
    int LatestVersion,
    int? PublishedVersion,
    DateTime? UpdatedAt);

public record QuestionnaireVersionDto(Guid Id, int Number, QuestionnaireVersionStatus Status, string DefinitionJson, int QuestionCount, DateTime? PublishedAtUtc);

public record QuestionnaireTemplateDetailDto(QuestionnaireTemplateSummaryDto Template, IReadOnlyList<QuestionnaireVersionDto> Versions);

public record CreateQuestionnaireTemplateRequest(string Key, string Title, string? Description, string? Industry, string DefinitionJson);

public record UpdateQuestionnaireTemplateRequest(string Title, string? Description, string? Industry, bool IsActive);

public record SaveQuestionnaireVersionRequest(string DefinitionJson);

public record QuestionnaireValidationDto(bool Valid, IReadOnlyList<QuestionnaireIssue> Issues, int QuestionCount);
