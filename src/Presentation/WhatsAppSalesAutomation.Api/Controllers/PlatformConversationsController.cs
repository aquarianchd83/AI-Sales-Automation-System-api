using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Tenant conversations page - PlatformSuperAdmin-only. WhatsApp chats between the platform's own number
/// and tenants: free text inside the 24-hour window, an approved platform template outside it. Replies come in through WebhooksController.</summary>
[ApiController]
[Route("api/v1/platform/conversations")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformConversationsController : ControllerBase
{
    private readonly IPlatformConversationService _conversations;
    private readonly ICurrentUserService _currentUser;

    public PlatformConversationsController(IPlatformConversationService conversations, ICurrentUserService currentUser)
    {
        _conversations = conversations;
        _currentUser = currentUser;
    }

    /// <summary>Most recent first. <paramref name="search"/> matches the tenant's name, the number or the WhatsApp profile name.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformConversationDto>>> List([FromQuery] string? search, CancellationToken cancellationToken)
        => Ok(await _conversations.ListAsync(search, cancellationToken));

    /// <summary>Tenants to start a chat with, and their numbers.</summary>
    [HttpGet("contacts")]
    public async Task<ActionResult<IReadOnlyList<PlatformConversationContactDto>>> Contacts([FromQuery] string? search, CancellationToken cancellationToken)
        => Ok(await _conversations.GetContactsAsync(search, cancellationToken));

    /// <summary>Approved, active platform templates - what can be sent when the 24-hour window is closed.</summary>
    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<PlatformConversationTemplateDto>>> Templates(CancellationToken cancellationToken)
        => Ok(await _conversations.GetTemplatesAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<PlatformConversationDto>> Start([FromBody] StartPlatformConversationRequest request, CancellationToken cancellationToken)
        => Ok(await _conversations.StartAsync(request, cancellationToken));

    /// <summary>The chat with its newest messages (or those before <paramref name="before"/>, for "load older"). Opening it marks it read.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlatformConversationDetailDto>> Get(Guid id, [FromQuery] DateTime? before, [FromQuery] int take, CancellationToken cancellationToken)
        => Ok(await _conversations.GetAsync(id, before is { } b ? DateTime.SpecifyKind(b, DateTimeKind.Utc) : null, take <= 0 ? 50 : take, cancellationToken));

    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<PlatformConversationMessageDto>> Send(Guid id, [FromBody] SendPlatformConversationMessageRequest request, CancellationToken cancellationToken)
        => Ok(await _conversations.SendAsync(id, request, _currentUser.UserId, _currentUser.Email, cancellationToken));

    /// <summary>The wa.me link to the platform number and the tenant's email - how to get the tenant to write first, so free text works.</summary>
    [HttpGet("{id:guid}/invite")]
    public async Task<ActionResult<PlatformConversationInviteDto>> Invite(Guid id, CancellationToken cancellationToken)
        => Ok(await _conversations.GetInviteAsync(id, cancellationToken));

    [HttpPost("{id:guid}/invite/email")]
    public async Task<ActionResult<PlatformConversationMessageDto>> EmailInvite(Guid id, [FromBody] SendPlatformConversationInviteRequest request, CancellationToken cancellationToken)
        => Ok(await _conversations.SendInviteEmailAsync(id, request, _currentUser.UserId, _currentUser.Email, cancellationToken));
}
