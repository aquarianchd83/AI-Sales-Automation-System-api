using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;

/// <summary>
/// The one place that decides what a fact is worth. A fact is usable by content only when it is Verified, not retired and not past its
/// date; everything else is a claim somebody still has to stand behind. An AI inference stays an unverified claim however it got its
/// status - the only way to cross over is a person verifying it, which is a different, audited action.
/// </summary>
public static class KnowledgeClassifier
{
    public static KnowledgeClassification Classify(BusinessFact fact, DateTime utcNow)
    {
        if (fact.Status == BusinessFactStatus.Retired)
            return KnowledgeClassification.Retired;

        if (fact.Status == BusinessFactStatus.Verified)
            return IsExpired(fact, utcNow) ? KnowledgeClassification.Expired : KnowledgeClassification.TenantApproved;

        return fact.Origin switch
        {
            BusinessFactOrigin.TenantProvided => KnowledgeClassification.TenantProvided,
            BusinessFactOrigin.SystemDerived => KnowledgeClassification.SystemDerived,
            _ => KnowledgeClassification.UnverifiedClaim
        };
    }

    public static bool IsExpired(BusinessFact fact, DateTime utcNow) => fact.ExpiresAtUtc is { } expires && expires <= utcNow;

    /// <summary>Whether content may assert this fact: verified by a person, current, and not retired.</summary>
    public static bool IsUsable(BusinessFact fact, DateTime utcNow) => Classify(fact, utcNow) == KnowledgeClassification.TenantApproved;
}
