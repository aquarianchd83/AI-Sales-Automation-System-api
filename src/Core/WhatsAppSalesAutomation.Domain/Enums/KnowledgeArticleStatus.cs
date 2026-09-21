namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>The knowledge article lifecycle. Replaces the three-value KnowledgeBaseArticleStatus.
///
/// ONLY <see cref="Published"/> is eligible for autonomous support answering. <see cref="Approved"/>
/// means "a human confirmed the content is right" but the article is not live yet - that gap is what
/// lets a policy change be approved on Monday and go live on the 1st, and it is why the ingestion
/// pipeline chunks and embeds Approved articles too (warm, so publishing is a flag flip rather than a
/// re-index) while retrieval still refuses to return them.
///
/// Persisted as a string (see KnowledgeBaseArticleConfiguration), so the numeric values here are not
/// load-bearing and existing Draft/Published/Archived rows carry over unchanged.</summary>
public enum KnowledgeArticleStatus
{
    /// <summary>Being written. Not chunked, not embedded, not retrievable.</summary>
    Draft = 0,

    /// <summary>With a reviewer. Not chunked, not embedded, not retrievable.</summary>
    InReview = 1,

    /// <summary>Content confirmed correct but not live. Chunked and embedded; never retrieved.</summary>
    Approved = 2,

    /// <summary>Live. The only status an autonomous answer may be grounded in.</summary>
    Published = 3,

    /// <summary>No longer true. Out of retrieval, still visible to admins and human agents - a human
    /// answering a ticket about last year's policy still needs to be able to read it.</summary>
    Deprecated = 4,

    /// <summary>Cold storage, kept for audit and lineage only.</summary>
    Archived = 5
}
