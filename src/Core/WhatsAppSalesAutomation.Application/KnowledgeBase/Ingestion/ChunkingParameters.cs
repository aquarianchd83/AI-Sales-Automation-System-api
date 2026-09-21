using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>
/// Per-<see cref="KnowledgeSourceType"/> chunk sizing (§H.5).
///
/// One setting does not suit every kind of knowledge. An FAQ is short and self-contained; a refund
/// policy is long and densely cross-referential, and splitting it at the same size would separate a
/// rule from the exception that qualifies it. The numbers come from the design document's table and
/// are code rather than configuration for the same reason <c>KnowledgeAuthority</c> is: changing them
/// changes what the AI is able to read in one piece.
/// </summary>
public readonly record struct ChunkingParameters(int TargetTokens, int MaxTokens, int MinTokens, int OverlapTokens)
{
    public static ChunkingParameters For(KnowledgeSourceType sourceType) => sourceType switch
    {
        // Long context needed; exceptions must stay near their rule.
        KnowledgeSourceType.PlatformPolicy or KnowledgeSourceType.LegalCompliance
            => new(700, 1400, 200, 120),

        // Conditions are dense here - this is the content the whole atomic-unit rule exists for.
        KnowledgeSourceType.RefundCancellationPolicy or KnowledgeSourceType.BillingRule
            => new(600, 1200, 200, 120),

        KnowledgeSourceType.AiUsageCreditRule or KnowledgeSourceType.WhatsAppPolicy
            or KnowledgeSourceType.LeadDiscoveryRule
            => new(550, 1100, 180, 100),

        KnowledgeSourceType.ProductDocumentation or KnowledgeSourceType.FeatureModuleDocumentation
            => new(500, 1000, 150, 80),

        // One question and its answer is one chunk, ideally without any packing at all.
        KnowledgeSourceType.ApprovedFaq
            => new(350, 700, 100, 40),

        // One diagnostic path per chunk.
        KnowledgeSourceType.TroubleshootingGuide
            => new(450, 900, 150, 60),

        // Small independent items.
        KnowledgeSourceType.KnownIssue or KnowledgeSourceType.ReleaseChangeNote
            => new(300, 600, 80, 30),

        _ => new(500, 1000, 150, 80)
    };
}
