namespace WhatsAppSalesAutomation.Application.MessageTemplates;

/// <summary>
/// <paramref name="WhatsAppTemplateStatus"/> only reflects Meta's real review outcome once
/// <paramref name="MetaTemplateId"/> is non-null - a template that has never been pushed also shows
/// "Pending" (its default), which looks identical to "pushed and genuinely awaiting Meta's review"
/// unless a consumer checks MetaTemplateId too. A frontend status column should treat MetaTemplateId
/// == null as its own distinct "Not synced" state, not fold it into WhatsAppTemplateStatus's Pending.
/// </summary>
public record MessageTemplateDto(
    Guid Id,
    string Name,
    string Language,
    string Category,
    string WhatsAppTemplateName,
    string WhatsAppTemplateStatus,
    string BodyText,
    bool IsActive,
    DateTime CreatedAt,
    string? MetaTemplateId);

public record CreateMessageTemplateRequest(
    string Name,
    string Language,
    string Category,
    string WhatsAppTemplateName,
    string BodyText);

/// <summary>
/// <paramref name="WhatsAppTemplateName"/> is optional and, when given, only accepted while the
/// template has never been pushed to Meta yet (MetaTemplateId is still null) - Meta does not allow
/// renaming a template after creation, so neither does this system once that has happened. Its main
/// use is fixing a non-Meta-compliant name (e.g. one entered before this system started validating
/// the format) before the next sync attempts to push it and fails again.
/// </summary>
public record UpdateMessageTemplateRequest(string BodyText, bool IsActive, string? WhatsAppTemplateName = null);

/// <summary>
/// A manual override of a template's review status - independent of MessageTemplateSyncJob's
/// automatic pull from Meta, for the rare case someone needs to force a value ahead of (or instead
/// of) the next sync, e.g. against a Simulated provider where there is no real Meta review to sync.
/// </summary>
public record ReviewMessageTemplateRequest(string Status);

/// <summary>
/// Result of one full sync cycle against Meta - see MessageTemplateService.SyncWithMetaAsync's own
/// doc comment for the two phases this covers: push (local -&gt; Meta, create/update content) then
/// pull (Meta -&gt; local, status only). Local Campaign templates are authoritative for name/body/
/// category throughout - Meta is never the source of truth for what a template says, only for
/// whether it has been reviewed and approved.
/// </summary>
public record TemplateSyncResultDto(
    int PushCandidateCount,
    int CreatedCount,
    int UpdatedCount,
    IReadOnlyList<TemplatePushFailureDto> PushFailures,
    int RemoteTemplateCount,
    int MatchedCount,
    int StatusUpdatedCount,
    IReadOnlyList<string> UnmatchedRemoteTemplateNames);

/// <summary>One local template Meta rejected during the push phase - e.g. an invalid name format
/// (Meta requires lowercase letters/digits/underscores only) or a policy violation. Reported, not
/// thrown, so one bad template does not abort the rest of the sync - same reasoning as
/// BulkPublishArticlesResultDto's FailedIds.</summary>
public record TemplatePushFailureDto(Guid TemplateId, string WhatsAppTemplateName, string ErrorMessage);

/// <summary>Result of syncing one template on demand (the per-row "Sync" button) - same push-then-pull
/// cycle as SyncWithMetaAsync, scoped to a single template. <paramref name="PushError"/> is set only
/// if this template's own push attempt failed (e.g. Meta rejected it), so a caller can surface that
/// specific reason immediately rather than the generic "did the status change" signal in Template.</summary>
public record MessageTemplateSyncOneResultDto(MessageTemplateDto Template, string? PushError);
