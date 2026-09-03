using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.MessageTemplates;

public interface IMessageTemplateService
{
    Task<PagedResult<MessageTemplateDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<MessageTemplateDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>New templates start <c>Pending</c>, same as a real Meta submission - see <see cref="ReviewAsync"/>.</summary>
    Task<MessageTemplateDto> CreateAsync(CreateMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task<MessageTemplateDto> UpdateAsync(Guid id, UpdateMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task<MessageTemplateDto> ReviewAsync(Guid id, ReviewMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The one place a local Campaign template and Meta's copy of it are reconciled, in two phases -
    /// local Campaign templates (this system) are the single surface for managing a template; Meta is
    /// only ever a downstream mirror plus the source of review status.
    ///
    /// Phase 1, push (local -&gt; Meta): every active template with no MetaTemplateId yet is created on
    /// Meta; every template whose BodyText has changed since its last successful push (tracked via
    /// LastPushedBodyText) is submitted as an edit. Name/Language/Category are only ever sent once, at
    /// creation - Meta does not allow changing them afterward, and neither does this system's own
    /// UpdateAsync, so there is nothing to push for them on a later run. A push failure (e.g. Meta
    /// rejects an invalid template name) is reported in PushFailures, not thrown - one bad template
    /// must not abort the rest of the cycle.
    ///
    /// Phase 2, pull (Meta -&gt; local): matched by (WhatsAppTemplateName, Language), case-insensitively.
    /// Status mapping: APPROVED -&gt; Approved; REJECTED -&gt; Rejected; PENDING/IN_APPEAL/PENDING_DELETION
    /// -&gt; Pending; anything else (PAUSED, DISABLED, or a status Meta adds later) -&gt; Rejected AND
    /// IsActive = false, since a template Meta will not currently deliver must not stay selectable by
    /// a campaign step regardless of what to call its state. A template that exists on Meta but not
    /// locally is reported in UnmatchedRemoteTemplateNames, never auto-created.
    /// </summary>
    Task<TemplateSyncResultDto> SyncWithMetaAsync(CancellationToken cancellationToken = default);

    /// <summary>Same push-then-pull cycle as <see cref="SyncWithMetaAsync"/>, scoped to one template -
    /// backs the per-row "Sync" button on the Message Templates admin page. Unlike the bulk sync, this
    /// pushes even an inactive template, since a user explicitly clicking Sync on one row is a
    /// deliberate action rather than the scheduled job's sweep across all active templates.</summary>
    Task<MessageTemplateSyncOneResultDto> SyncOneAsync(Guid id, CancellationToken cancellationToken = default);
}
