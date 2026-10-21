using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>One credit scenario as the Credit costs screen describes it. <paramref name="DefaultUnits"/> is what the action costs in CREDITS until the platform admin
/// changes it: per unit for a scenario whose code sizes its own spend (a message, a reply, a candidate, a studio step), per action for product knowledge
/// (<paramref name="UnitsConfigurable"/>). <paramref name="UnitLabel"/> names the unit for the placeholder shown on each feature.</summary>
public sealed record CreditScenarioInfo(
    CreditScenario Scenario,
    string Title,
    string Area,
    string Description,
    QuotaType QuotaType,
    CreditPayer DefaultPayer,
    bool UnitsConfigurable,
    decimal DefaultUnits,
    string UnitLabel);

/// <summary>Every scenario the application spends credits for, with the defaults that apply until the platform admin changes them. Every scenario
/// is paid by the tenant's one credit wallet unless the platform admin chooses to cover it.</summary>
public static class CreditScenarioCatalog
{
    public const string ProductKnowledgeReference = "ProductKnowledge";

    public static readonly IReadOnlyList<CreditScenarioInfo> All = new CreditScenarioInfo[]
    {
        new(CreditScenario.CustomerChatReply, "Customer chat reply", "Customer chat",
            "The AI answering a customer in WhatsApp. One AI conversation per customer message.",
            QuotaType.Credits, CreditPayer.Tenant, false, 1m, "reply"),
        new(CreditScenario.WhatsAppTemplateSend, "WhatsApp template send", "Messaging",
            "A billable template message sent by an agent, a campaign or a lead follow-up. Free-form replies inside the 24-hour window cost nothing.",
            QuotaType.Credits, CreditPayer.Tenant, false, 1m, "message"),
        new(CreditScenario.LeadDiscovery, "Lead discovery", "Lead discovery",
            "Each candidate business a discovery run evaluates - new, duplicate or rejected alike.",
            QuotaType.Credits, CreditPayer.Tenant, false, 1m, "candidate"),
        new(CreditScenario.ContentStudioGeneration, "Content Studio generation", "Content Studio",
            "Topic ideas, a script, a scene breakdown, a voice-over or a generated illustration.",
            QuotaType.Credits, CreditPayer.Tenant, false, 1m, "step"),
        new(CreditScenario.ContentStudioVideoRender, "Content Studio video render", "Content Studio",
            "Rendering a finished video.",
            QuotaType.Credits, CreditPayer.Tenant, false, 10m, "video"),
        new(CreditScenario.ProductKnowledgeReading, "Product knowledge: reading sources", "Product knowledge",
            "Reading uploaded documents, scans and websites and building the product knowledge from them.",
            QuotaType.Credits, CreditPayer.Tenant, true, 5m, "action"),
        new(CreditScenario.ProductKnowledgeAnswering, "Product knowledge: drafting and answering", "Product knowledge",
            "Drafting answers, checking for conflicts and missing information, and answering questions from the sources.",
            QuotaType.Credits, CreditPayer.Tenant, true, 1m, "action"),
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
            "StudioActivity" => CreditScenario.ContentStudioGeneration, // a video render names its own scenario (see StudioMeteringService)
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
