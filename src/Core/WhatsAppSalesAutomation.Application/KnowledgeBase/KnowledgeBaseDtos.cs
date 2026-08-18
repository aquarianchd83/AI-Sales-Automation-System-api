namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public record KnowledgeBaseArticleDto(
    Guid Id,
    string Title,
    string? Category,
    string SourceType,
    string Content,
    string Status,
    int Version,
    Guid? ApprovedBy,
    int ChunkCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record CreateKnowledgeBaseArticleRequest(string Title, string? Category, string Content, string SourceType);

/// <summary>Editing Content bumps KnowledgeBaseArticle.Version but does not re-chunk/embed by itself -
/// an already-Published article keeps serving its old chunks (embedded from the old Version) until
/// PublishAsync or ReindexAsync is called again, so an edit can be saved as a draft-in-progress without
/// affecting what the AI is currently grounded on.</summary>
public record UpdateKnowledgeBaseArticleRequest(string Title, string? Category, string Content);

/// <summary>What RetrieveRelevantChunksAsync returns - the chunk's text plus how well it matched the
/// query, for both grounding the AI's prompt and recording AiInteractionSource rows.</summary>
public record RetrievedChunk(Guid ChunkId, Guid ArticleId, string Text, double RelevanceScore);
