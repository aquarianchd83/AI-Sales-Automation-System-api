using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Helps the platform operator write the readiness rules (the <c>ContentStudio:ReadinessRules</c> setting): the built-in rules to start
/// from, and a check of a rule set before it is saved - because a rule set that does not pass is ignored and the built-in one used.
/// PlatformSuperAdmin only.
/// </summary>
[ApiController]
[Route("api/v1/platform/content-studio/readiness-rules")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformStudioReadinessController : ControllerBase
{
    /// <summary>The built-in rules, as the JSON the setting takes. Edit a copy of this.</summary>
    [HttpGet("default")]
    public ActionResult<DefaultReadinessRulesDto> Default() => Ok(new DefaultReadinessRulesDto(ReadinessRules.DefaultJson));

    /// <summary>Whether a rule set would be accepted, and if not, every reason why.</summary>
    [HttpPost("validate")]
    public ActionResult<ReadinessRulesValidationDto> Validate([FromBody] ValidateReadinessRulesRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Json))
            return Ok(new ReadinessRulesValidationDto(true, Array.Empty<string>(), "Empty: the built-in rules are used."));

        try
        {
            var rules = JsonSerializer.Deserialize<ReadinessRuleSet>(request.Json, ReadinessRules.JsonOptions);
            if (rules is null)
                return Ok(new ReadinessRulesValidationDto(false, new[] { "The rules are empty." }, null));

            var problems = ReadinessRules.Validate(rules);
            return Ok(new ReadinessRulesValidationDto(
                problems.Count == 0, problems, problems.Count == 0 ? $"{rules.ContentTypes.Count} kinds of content, {rules.Requirements.Count} requirements." : null));
        }
        catch (JsonException ex)
        {
            return Ok(new ReadinessRulesValidationDto(false, new[] { $"Not valid JSON: {ex.Message}" }, null));
        }
    }
}

public record DefaultReadinessRulesDto(string Json);

public record ValidateReadinessRulesRequest(string? Json);

public record ReadinessRulesValidationDto(bool Valid, IReadOnlyList<string> Problems, string? Summary);
