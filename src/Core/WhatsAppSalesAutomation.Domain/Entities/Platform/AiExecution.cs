using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// One attempt to run an AI task: which provider and model, whether it was a live call or the simulator, how long it took, what it cost, and
/// how it ended. Written for every attempt - a fallback is a second row under the same <see cref="CorrelationId"/>. Deliberately holds
/// no prompt, no document text and no key: it is an operational record, readable by the platform admin, not a copy of the data.
/// Platform-owned: <see cref="TenantId"/> only says whose request it served and is never used to filter.
/// </summary>
public class AiExecution : BaseEntity
{
    /// <summary>Groups the attempts (first try, retry, fallbacks) of one task run.</summary>
    public Guid CorrelationId { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>What was asked, e.g. "GroundedQuestionAnswering", "ConnectionCheck", "Benchmark".</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>The AI feature whose engine choice routed it (e.g. "ProductKnowledge"), if any.</summary>
    public string? Feature { get; set; }

    /// <summary>The provider the admin's choice pointed at; <see cref="Provider"/> differs when a fallback ran.</summary>
    public string RequestedProvider { get; set; } = string.Empty;

    /// <summary>OpenAI, Anthropic, Google, or Simulator.</summary>
    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    public AiExecutionMode Mode { get; set; }

    public AiExecutionStatus Status { get; set; }

    /// <summary>1 for the first attempt of a run, then 2, 3...</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>For an attempt made because another provider failed: the one that failed.</summary>
    public string? FallbackFromProvider { get; set; }

    public int DurationMs { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary>Estimated from the token counts and the price table on the Integrations page. Null when the provider returned no counts.</summary>
    public decimal? EstimatedCostUsd { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>A short, scrubbed reason. Never a provider response body.</summary>
    public string? ErrorSummary { get; set; }

    /// <summary>A small result summary for checks and benchmarks (JSON). Not used for ordinary tasks.</summary>
    public string? Detail { get; set; }
}
