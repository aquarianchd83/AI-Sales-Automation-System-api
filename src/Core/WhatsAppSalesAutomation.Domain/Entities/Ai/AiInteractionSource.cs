using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Ai;

/// <summary>Join row recording which KnowledgeBaseChunks were retrieved and cited as grounding for one
/// AiInteraction - the audit trail for "why did the AI say that", and what a future "show sources"
/// feature in the agent inbox would read.</summary>
public class AiInteractionSource : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid AiInteractionId { get; set; }

    /// <summary>The chunk that was cited. Null once that chunk has been replaced by a re-index (the swap deletes the old chunks); the citation then lives on
    /// in <see cref="KnowledgeItemId"/> and <see cref="KnowledgeVersionId"/> for product knowledge, and in <see cref="RelevanceScore"/>.</summary>
    public Guid? KnowledgeBaseChunkId { get; set; }

    /// <summary>For product knowledge served to chat: the published item cited, copied here so the citation survives the chunk being replaced.</summary>
    public Guid? KnowledgeItemId { get; set; }

    /// <summary>For the same: the published version that item was served from.</summary>
    public Guid? KnowledgeVersionId { get; set; }

    /// <summary>Cosine similarity to the query embedding at retrieval time (0.0-1.0), snapshotted here
    /// since the chunk's own embedding can change on re-index and would otherwise make this
    /// unreproducible after the fact.</summary>
    public double RelevanceScore { get; set; }
}
