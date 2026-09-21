namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where an ingestion job is (G.9). Persisted as a string so states can be inserted without
/// renumbering. Terminal states are Completed, Failed and Rejected; AwaitingApproval is a resting
/// state, not a terminal one - a human decision moves it on.</summary>
public enum KnowledgeIngestionState
{
    Queued = 0,
    Validating = 1,
    Extracting = 2,
    Cleaning = 3,
    Enriching = 4,

    /// <summary>The human gate: the content tripped a security flag, and indexing will not proceed
    /// until a reviewer has looked at the findings and allowed it.</summary>
    AwaitingApproval = 5,

    Chunking = 6,
    Embedding = 7,
    Indexing = 8,
    Verifying = 9,
    Completed = 10,

    /// <summary>A technical failure. Resumable: staged chunks are kept and the next run continues.</summary>
    Failed = 11,

    /// <summary>A content decision - blocked by the injection scan, or not in an indexable status.
    /// Re-running without changing the article changes nothing, unlike Failed.</summary>
    Rejected = 12
}
