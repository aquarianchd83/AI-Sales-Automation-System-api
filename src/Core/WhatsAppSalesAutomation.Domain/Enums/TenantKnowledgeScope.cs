namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Whether an article is platform-owned or tenant-owned.
///
/// Redundant with <c>(TenantId IS NULL)</c> by construction, and stored anyway for two concrete
/// reasons: the CK_KBArticles_TenantAuthority check constraint can then reference a column rather
/// than a nullability test, and admin listings can filter on it without a computed expression.
/// CK_KBArticles_ScopeMatchesTenant keeps the two in agreement, so the redundancy cannot drift.</summary>
public enum TenantKnowledgeScope
{
    Global = 0,
    Tenant = 1
}
