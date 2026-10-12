using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The platform operator's questionnaire templates for the AI Content Studio: what every tenant (or every tenant of an industry) is
/// asked. PlatformSuperAdmin only. A published version is never edited - a change is a new version - and publishing is audited, because
/// it changes what tenants are asked from then on.
/// </summary>
[ApiController]
[Route("api/v1/platform/content-studio/questionnaire-templates")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformStudioQuestionnaireController : ControllerBase
{
    private readonly IQuestionnaireTemplateService _templates;
    private readonly ICurrentUserService _currentUser;

    public PlatformStudioQuestionnaireController(IQuestionnaireTemplateService templates, ICurrentUserService currentUser)
    {
        _templates = templates;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<QuestionnaireTemplateSummaryDto>>> List(CancellationToken cancellationToken)
        => Ok(await _templates.ListAsync(cancellationToken));

    /// <summary>A starting definition to build a new template from.</summary>
    [HttpGet("starter")]
    public ActionResult<object> Starter() => Ok(new { definitionJson = _templates.GetStarterDefinition() });

    [HttpPost("validate")]
    public ActionResult<QuestionnaireValidationDto> Validate([FromBody] SaveQuestionnaireVersionRequest request)
        => Ok(_templates.Validate(request.DefinitionJson));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuestionnaireTemplateDetailDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _templates.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<QuestionnaireTemplateDetailDto>> Create([FromBody] CreateQuestionnaireTemplateRequest request, CancellationToken cancellationToken)
        => Ok(await _templates.CreateAsync(request, ActorId, ActorEmail, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<QuestionnaireTemplateDetailDto>> Update(Guid id, [FromBody] UpdateQuestionnaireTemplateRequest request, CancellationToken cancellationToken)
        => Ok(await _templates.UpdateAsync(id, request, ActorId, ActorEmail, cancellationToken));

    [HttpPost("{id:guid}/versions")]
    public async Task<ActionResult<QuestionnaireTemplateDetailDto>> CreateVersion(Guid id, CancellationToken cancellationToken)
        => Ok(await _templates.CreateVersionAsync(id, ActorId, ActorEmail, cancellationToken));

    [HttpPut("{id:guid}/versions/{number:int}")]
    public async Task<ActionResult<QuestionnaireTemplateDetailDto>> SaveVersion(Guid id, int number, [FromBody] SaveQuestionnaireVersionRequest request, CancellationToken cancellationToken)
        => Ok(await _templates.SaveVersionAsync(id, number, request, ActorId, ActorEmail, cancellationToken));

    [HttpPost("{id:guid}/versions/{number:int}/publish")]
    public async Task<ActionResult<QuestionnaireTemplateDetailDto>> Publish(Guid id, int number, CancellationToken cancellationToken)
        => Ok(await _templates.PublishVersionAsync(id, number, ActorId, ActorEmail, cancellationToken));

    private Guid ActorId => _currentUser.UserId ?? throw new InvalidOperationException("A platform action needs a signed-in user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
