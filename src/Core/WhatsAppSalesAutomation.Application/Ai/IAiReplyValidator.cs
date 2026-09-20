using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>
/// Checks the model's output before any of it reaches a customer.
///
/// The model is not treated as trustworthy here, and that is the point: everything it returns is a
/// claim. Some claims are cheap to check (is this intent in the enum? is this field key in the
/// tenant's schema?) and a few are the difference between a good reply and an embarrassing one (does
/// this reply leak how the agent works? does it quote a lead score the tenant said never to mention?).
///
/// A failed check never silently rewrites the reply. It blocks it and escalates, because a reply that
/// had to be patched to be safe is one a human should have sent.
/// </summary>
public interface IAiReplyValidator
{
    ValidatedReply Validate(AiReplyResult result, AiConversationContext context);
}

/// <summary>
/// The model's output after checking: the parts that survived, plus whether it may be sent at all.
///
/// <paramref name="ExtractedFields"/> is the accepted subset - a claim about a field key this tenant
/// does not have is dropped here rather than being allowed to fail later in capture, so the rest of
/// the turn still counts.
/// </summary>
public record ValidatedReply(
    bool CanSend,
    IReadOnlyList<ValidationFailure> Failures,
    string ResponseText,
    string DetectedIntent,
    double ConfidenceScore,
    IReadOnlyList<AiExtractedField> ExtractedFields,
    IReadOnlyList<Guid> CitedChunkIds,
    string UpdatedSummary,
    bool BuyingIntentDetected,
    bool HumanRequested,
    bool OptOutRequested,
    string? AskedFieldKey,
    string? DetectedLanguage,
    string? AgentNote)
{
    public bool HasFailures => Failures.Count > 0;

    /// <summary>One line naming every check that failed, for the handoff note and the audit row.</summary>
    public string FailureSummary => string.Join("; ", Failures.Select(f => $"{f.Code}: {f.Detail}"));
}

/// <summary>A check that did not pass. <paramref name="Blocking"/> distinguishes "this reply must not
/// be sent" from "this part of the output was unusable but the reply is fine" - a hallucinated field
/// key is the latter, a leaked system prompt is the former.</summary>
public record ValidationFailure(string Code, string Detail, bool Blocking);
