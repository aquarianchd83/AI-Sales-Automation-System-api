using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <param name="Role">"customer" (what the tester typed) or "assistant" (what the AI replied).</param>
/// <param name="Kind">For an assistant turn, the Outcome the server returned for it, echoed back so the next turn knows (Answered, Clarifying, HandedOver).</param>
public record TestChatTurn(string Role, string Text, string? Kind = null);

public record TestChatRequest(string? Message, IReadOnlyList<TestChatTurn>? History);

/// <summary>Where the answer came from: an approved item of the published version, and how close it was to the question.</summary>
public record TestChatSource(Guid ItemId, int VersionNumber, string Question, string Excerpt, double Relevance, bool IsMaterialClaim, bool Cited);

/// <param name="Reply">What the customer would be sent. Null when nothing would be sent (a person would take over).</param>
/// <param name="Outcome">Answered (from the published knowledge), Clarifying (nothing approved covers it, so one question), HandedOver (a person would take over).</param>
/// <param name="Why">One sentence on what happened, for the tester; never shown to a customer.</param>
/// <param name="PlatformNotice">Set when the platform itself is not fully set up (an AI provider is Simulated): tells the tester to carry on and to contact the product owner, who has already been alerted.</param>
/// <param name="DraftBlocked">When a draft was written but would not be sent, what it was and which checks stopped it.</param>
public record TestChatReply(string? Reply, string Outcome, string Why, IReadOnlyList<TestChatSource> Sources, string? DraftBlocked, string DetectedIntent, string? PlatformNotice = null);

/// <summary>
/// A tester talking to the AI as a customer would, answered only from the tenant's published product knowledge - the same retrieval, prompt and reply checks as
/// live chat, so what is said here is what customers would be told. Nothing is stored or sent: no conversation, lead, WhatsApp message or credit is used.
/// It works whether or not the platform administrator has switched live chat over, so the tenant can try their knowledge first.
/// </summary>
public interface IProductTestChat
{
    Task<TestChatReply> AskAsync(Guid tenantId, TestChatRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProductTestChat : IProductTestChat
{
    public const int MaxMessageChars = 500;
    public const int MaxHistoryTurns = 12;

    private readonly IApplicationDbContext _db;
    private readonly IKnowledgeBaseService _knowledge;
    private readonly IAiService _ai;
    private readonly IAiReplyValidator _validator;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly IPlatformAiConfigProvider _platformAi;
    private readonly IPlatformSetupAlerts _alerts;

    public ProductTestChat(IApplicationDbContext db, IKnowledgeBaseService knowledge, IAiService ai, IAiReplyValidator validator, ITenantConfigOverrideProvider config,
        IPlatformAiConfigProvider platformAi, IPlatformSetupAlerts alerts)
    {
        _db = db;
        _knowledge = knowledge;
        _ai = ai;
        _validator = validator;
        _config = config;
        _platformAi = platformAi;
        _alerts = alerts;
    }

    public async Task<TestChatReply> AskAsync(Guid tenantId, TestChatRequest request, CancellationToken ct = default)
    {
        var reply = await AskCoreAsync(tenantId, request, ct);

        // The tester can carry on - the notice only says why answers may be off and who to talk to. The product owner is alerted once (see IPlatformSetupAlerts).
        var ai = _platformAi.Get();
        if (!IsSimulated(ai.Provider) && !IsSimulated(ai.EmbeddingProvider))
            return reply;

        await _alerts.ReportSimulatedAiAsync(tenantId, "the test chat", ct);
        return reply with { PlatformNotice = SimulatedNotice };
    }

    public const string SimulatedNotice =
        "Your AI assistant is running in test mode, so it may not find the knowledge you published. You can carry on, but please contact your product owner to finish the AI setup. "
        + "We have already raised an urgent ticket and sent them an alert.";

    private static bool IsSimulated(string? provider) => string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "Simulated", StringComparison.OrdinalIgnoreCase);

    private async Task<TestChatReply> AskCoreAsync(Guid tenantId, TestChatRequest request, CancellationToken ct)
    {
        var message = (request?.Message ?? string.Empty).Trim();
        if (message.Length == 0)
            throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure("message", "Type a message first.") });
        if (message.Length > MaxMessageChars)
            throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure("message", $"Keep the message under {MaxMessageChars} characters.") });

        var live = await _db.KnowledgeVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.ServingStatus == KnowledgeServingStatus.Live).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct)
            ?? throw new ConflictException("Publish your product knowledge first - there is nothing live for chat to answer from yet.");

        var history = (request!.History ?? Array.Empty<TestChatTurn>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Text)).TakeLast(MaxHistoryTurns)
            .Select(t => new TestChatTurn(IsAssistant(t.Role) ? "assistant" : "customer", t.Text.Trim().Length > 1000 ? t.Text.Trim()[..1000] : t.Text.Trim(), t.Kind))
            .ToList();

        // A short follow-up ("and how long does it take?") only makes sense with what came before, so the search sees the last customer messages too.
        var retrieved = await _knowledge.RetrievePublishedProductKnowledgeAsync(SearchText(message, history), ct);
        var snippets = retrieved.Select(r => new AiKnowledgeSnippet(r.ChunkId, r.Text, r.RelevanceScore, r.KnowledgeItemId, r.KnowledgeVersionId, r.IsMaterialClaim)).ToList();

        var context = new AiConversationContext(
            Guid.NewGuid(), "the customer", message,
            history.Select(t => new AiConversationTurn(t.Role == "assistant" ? MessageDirection.Outbound : MessageDirection.Inbound, t.Text, DateTime.UtcNow)).ToList(),
            snippets, null, await BusinessAsync(tenantId, ct),
            Array.Empty<AiQualificationField>(), Array.Empty<AiCapturedField>(), Array.Empty<AiQualificationField>(),
            QualificationPaused: true, PreferredLanguage: null, Crm: null, ProductKnowledgeServing: true);

        AiReplyResult result;
        try
        {
            result = await _ai.GetResponseAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConflictException("The AI could not answer just now. Try again in a moment.");
        }

        if (result.ProviderFailed)
            throw new ConflictException("The AI could not answer just now. Try again in a moment.");

        var validated = _validator.Validate(result, context);
        var options = await _config.GetAiOptionsAsync(ct);
        var number = live.Number;
        var cited = validated.CitedChunkIds.ToHashSet();
        var itemQuestions = await ItemQuestionsAsync(tenantId, snippets, ct);
        var sources = snippets.Select(s => new TestChatSource(
            s.KnowledgeItemId ?? Guid.Empty, number, itemQuestions.GetValueOrDefault(s.KnowledgeItemId ?? Guid.Empty, "Published answer"), Excerpt(s.Text),
            Math.Round(s.RelevanceScore, 2), s.IsMaterialClaim, cited.Contains(s.ChunkId))).ToList();

        // The same call live chat makes: second miss in a row, or any of the usual reasons, and a person takes over instead of the AI replying.
        var missAgain = snippets.Count == 0 && validated.Failures.Any(f => f.Code == AiReplyValidator.NoApprovedKnowledgeCode)
            && history.LastOrDefault(t => t.Role == "assistant")?.Kind == "Clarifying";
        var lowConfidence = validated.ConfidenceScore < options.ConfidenceThreshold;
        var escalationIntent = options.EscalationIntents.Any(i => string.Equals(i, validated.DetectedIntent, StringComparison.OrdinalIgnoreCase));
        var handOver = missAgain || !validated.CanSend || validated.HumanRequested || lowConfidence || escalationIntent;

        if (handOver)
        {
            var why = missAgain ? "Nothing you published covers this, and the previous question did not help, so a person would take over."
                : !validated.CanSend ? "The AI's draft failed a safety check, so it would not be sent and a person would take over."
                : validated.HumanRequested ? "The customer asked for a person."
                : escalationIntent ? $"This kind of message ({validated.DetectedIntent}) is passed to a person."
                : "The AI was not confident enough, so a person would take over.";
            var blocked = !validated.CanSend && !string.IsNullOrWhiteSpace(validated.ResponseText)
                ? $"{validated.ResponseText} ({string.Join("; ", validated.Failures.Where(f => f.Blocking).Select(f => f.Detail))})"
                : null;
            return new TestChatReply(null, "HandedOver", why, sources, blocked, validated.DetectedIntent);
        }

        if (snippets.Count == 0)
            return new TestChatReply(validated.ResponseText, "Clarifying", "Nothing you published covers this, so the AI asks one question instead of guessing.", sources, null, validated.DetectedIntent);

        return new TestChatReply(validated.ResponseText, "Answered", $"Answered from {snippets.Count} approved answer{(snippets.Count == 1 ? string.Empty : "s")} in version {number}.", sources, null, validated.DetectedIntent);
    }

    private static bool IsAssistant(string? role) => string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "ai", StringComparison.OrdinalIgnoreCase);

    private static string SearchText(string message, List<TestChatTurn> history)
    {
        if (message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 12)
            return message;
        var earlier = history.Where(t => t.Role == "customer").TakeLast(2).Select(t => t.Text);
        return string.Join(" ", earlier.Append(message));
    }

    private static string Excerpt(string text)
    {
        var line = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return line.Length <= 220 ? line : line[..220] + "…";
    }

    private async Task<Dictionary<Guid, string>> ItemQuestionsAsync(Guid tenantId, List<AiKnowledgeSnippet> snippets, CancellationToken ct)
    {
        var ids = snippets.Where(s => s.KnowledgeItemId != null).Select(s => s.KnowledgeItemId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();
        return await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Question, ct);
    }

    private async Task<AiBusinessProfile> BusinessAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        return tenant is null
            ? new AiBusinessProfile("the business", null, null, null, null, null, null, ConversationGoal.Enquiry, false)
            : new AiBusinessProfile(
                tenant.Name, string.IsNullOrWhiteSpace(tenant.IndustrySubcategory) ? tenant.Industry : $"{tenant.Industry} ({tenant.IndustrySubcategory})",
                tenant.BusinessLocation, tenant.WebsiteUrl, tenant.WorkingHours, tenant.ProductName, tenant.BusinessDescription, tenant.AiConversationGoal, false);
    }
}
