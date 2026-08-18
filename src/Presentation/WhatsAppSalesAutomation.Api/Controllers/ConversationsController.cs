using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Conversations;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly ICurrentUserService _currentUser;

    public ConversationsController(IConversationService conversationService, ICurrentUserService currentUser)
    {
        _conversationService = conversationService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<ConversationDto>>> GetPaged(
        [FromQuery] PagedRequest request, [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await _conversationService.GetPagedAsync(request, status, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConversationDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _conversationService.GetByIdAsync(id, cancellationToken));

    /// <summary>The transcript - both directions, campaign-originated and agent-originated alike.</summary>
    [HttpGet("{id:guid}/messages")]
    public async Task<ActionResult<PagedResult<ConversationMessageDto>>> GetMessages(
        Guid id, [FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _conversationService.GetMessagesAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/mode")]
    public async Task<ActionResult<ConversationDto>> ChangeMode(Guid id, [FromBody] ChangeConversationModeRequest request, CancellationToken cancellationToken)
        => Ok(await _conversationService.ChangeModeAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<ConversationDto>> Assign(Guid id, [FromBody] AssignConversationRequest request, CancellationToken cancellationToken)
        => Ok(await _conversationService.AssignAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/close")]
    public async Task<ActionResult<ConversationDto>> Close(Guid id, CancellationToken cancellationToken)
        => Ok(await _conversationService.CloseAsync(id, cancellationToken));

    /// <summary>The manual agent reply - see IConversationService.SendMessageAsync for the customer
    /// service window / template rules this enforces.</summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<ConversationMessageDto>> SendMessage(Guid id, [FromBody] SendConversationMessageRequest request, CancellationToken cancellationToken)
    {
        var sentByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _conversationService.SendMessageAsync(id, request, sentByUserId, cancellationToken));
    }
}
