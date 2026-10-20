namespace WhatsAppSalesAutomation.Domain.Constants;

/// <summary>Names the platform manages itself for the tenant's published product knowledge.</summary>
public static class ProductKnowledgeSystem
{
    /// <summary><see cref="Entities.KnowledgeBase.KnowledgeBaseArticle.SystemKey"/> of the one article per tenant that holds the published knowledge version
    /// as chunks for customer chat. It is written only by the platform; a tenant cannot edit, publish, unpublish or delete it.</summary>
    public const string ArticleKey = "ProductKnowledge";

    public const string ArticleTitle = "Published product knowledge";
}
