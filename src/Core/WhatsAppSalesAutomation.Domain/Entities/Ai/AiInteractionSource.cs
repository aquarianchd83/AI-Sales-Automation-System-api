using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Ai;

/// <summary>Join row recording which KnowledgeBaseChunks were retrieved and cited as grounding for one
/// AiInteraction - the audit trail for "why did the AI say that", and what a future "show sources"
/// feature in the agent inbox would read.</summary>
public class AiInteractionSource : BaseEntity
{
    public Guid AiInteractionId { get; set; }

    public Guid KnowledgeBaseChunkId { get; set; }

    /// <summary>Cosine similarity to the query embedding at retrieval time (0.0-1.0), snapshotted here
    /// since the chunk's own embedding can change on re-index and would otherwise make this
    /// unreproducible after the fact.</summary>
    public double RelevanceScore { get; set; }
}
