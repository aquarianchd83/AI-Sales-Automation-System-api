using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>One credit scenario as the Credit Payers screen describes it. <paramref name="UnitsConfigurable"/> is true when the platform sets
/// the cost of one action (product knowledge); otherwise the code that performs the action sizes the spend itself.</summary>
public sealed record CreditScenarioInfo(
    CreditScenario Scenario,
    string Title,
    string Area,
    string Description,
    QuotaType QuotaType,
    CreditPayer DefaultPayer,
    bool UnitsConfigurable,
    decimal DefaultUnits);

/// <summary>Every scenario the application spends credits for, with the defaults that apply until the platform admin changes them. The
/// defaults keep today's behaviour: existing spends are charged to the tenant, and product knowledge - which charges no credit today - stays free.</summary>
public static class CreditScenarioCatalog
{
    public const string ProductKnowledgeReference = "ProductKnowledge";

    public static readonly IReadOnlyList<CreditScenarioInfo> All = new CreditScenarioInfo[]
    {
        new(CreditScenario.CustomerChatReply, "Customer chat reply", "Customer chat",
            "The AI answering a customer in WhatsApp. One AI conversation per customer message.",
            QuotaType.AiConversations, CreditPayer.Tenant, false, 1m),
        new(CreditScenario.WhatsAppTemplateSend, "WhatsApp template send", "Messaging",
            "A billable template message sent by an agent, a campaign or a lead follow-up. Free-form replies inside the 24-hour window cost nothing.",
            QuotaType.WhatsAppMessages, CreditPayer.Tenant, false, 1m),
        new(CreditScenario.LeadDiscovery, "Lead discovery", "Lead discovery",
            "Each candidate business a discovery run evaluates - new, duplicate or rejected alike.",
            QuotaType.LeadCandidates, CreditPayer.Tenant, false, 1m),
        new(CreditScenario.ContentStudioGeneration, "Content Studio generation", "Content Studio",
            "Topic ideas, a script, a scene breakdown, a voice-over or a generated illustration.",
            QuotaType.ContentGenerations, CreditPayer.Tenant, false, 1m),
        new(CreditScenario.ContentStudioVideoRender, "Content Studio video render", "Content Studio",
            "Rendering a finished video.",
            QuotaType.VideoRenders, CreditPayer.Tenant, false, 1m),
        new(CreditScenario.ProductKnowledgeReading, "Product knowledge: reading sources", "Product knowledge",
            "Reading uploaded documents, scans and websites and building the product knowledge from them. Free today.",
            QuotaType.AiConversations, CreditPayer.Platform, true, 0m),
        new(CreditScenario.ProductKnowledgeAnswering, "Product knowledge: drafting and answering", "Product knowledge",
            "Drafting answers, checking for conflicts and missing information, and answering questions from the sources. Free today.",
            QuotaType.AiConversations, CreditPayer.Platform, true, 0m),
    };

    public static CreditScenarioInfo For(CreditScenario scenario) => All.Single(i => i.Scenario == scenario);

    /// <summary>The scenario a spend belongs to: the one it names, or the one its reference type implies. Null when it is not a scenario
    /// the platform can pick a payer for (the spend then goes straight to the tenant, untouched).</summary>
    public static CreditScenario? Classify(ConsumeRequest request)
    {
        if (request.Scenario is { } named)
            return named;

        return request.ReferenceType switch
        {
            "Message" => CreditScenario.WhatsAppTemplateSend,
            "AiTransaction" or "AiInteraction" => CreditScenario.CustomerChatReply,
            "LeadDiscoveryRun" => CreditScenario.LeadDiscovery,
            "StudioActivity" => request.QuotaType == QuotaType.VideoRenders ? CreditScenario.ContentStudioVideoRender : CreditScenario.ContentStudioGeneration,
            _ => null
        };
    }

    /// <summary>The scenario an AI task's cost belongs to, for the provider-cost column on the screen. Null for tasks that are not a scenario.</summary>
    public static CreditScenario? ForAiTask(string? task) => task switch
    {
        AiTaskKinds.ConversationReply => CreditScenario.CustomerChatReply,
        AiTaskKinds.LeadDiscovery => CreditScenario.LeadDiscovery,
        AiTaskKinds.DocumentAnalysis or AiTaskKinds.ProductInfoExtraction or AiTaskKinds.ScannedDocumentReading or AiTaskKinds.KnowledgeConsolidation
            => CreditScenario.ProductKnowledgeReading,
        AiTaskKinds.GroundedQuestionAnswering or AiTaskKinds.CustomQuestionAnswering or AiTaskKinds.SuggestedAnswerGeneration
            or AiTaskKinds.QuestionnaireGeneration or AiTaskKinds.BenefitAnalysis or AiTaskKinds.MissingInformationDetection
            or AiTaskKinds.ConflictIdentification or AiTaskKinds.KnowledgeDraft
            => CreditScenario.ProductKnowledgeAnswering,
        _ => null
    };
}
