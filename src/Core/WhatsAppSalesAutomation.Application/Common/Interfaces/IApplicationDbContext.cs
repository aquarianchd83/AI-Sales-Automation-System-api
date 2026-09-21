using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Media;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Entities.Webhooks;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// The Application layer's view of the database. Services depend on this instead of a
/// generic repository/unit-of-work pair - EF Core's DbContext already <i>is</i> both,
/// so wrapping it further would just be ceremony. Identity's own Users/Roles DbSets are
/// reached through <see cref="Microsoft.AspNetCore.Identity.UserManager{TUser}"/> /
/// <see cref="Microsoft.AspNetCore.Identity.RoleManager{TRole}"/> instead of here.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Customer> Customers { get; }

    DbSet<CustomerTag> CustomerTags { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<MediaAsset> MediaAssets { get; }

    DbSet<MessageTemplate> MessageTemplates { get; }

    DbSet<Campaign> Campaigns { get; }

    DbSet<CampaignStep> CampaignSteps { get; }

    DbSet<CampaignStepMedia> CampaignStepMedia { get; }

    DbSet<CampaignCustomer> CampaignCustomers { get; }

    DbSet<Message> Messages { get; }

    DbSet<Conversation> Conversations { get; }

    DbSet<HumanHandoff> HumanHandoffs { get; }

    DbSet<WebhookEvent> WebhookEvents { get; }

    DbSet<AiInteraction> AiInteractions { get; }

    DbSet<AiInteractionSource> AiInteractionSources { get; }

    DbSet<AiInteractionValidationFailure> AiInteractionValidationFailures { get; }

    DbSet<KnowledgeBaseArticle> KnowledgeBaseArticles { get; }

    DbSet<KnowledgeBaseChunk> KnowledgeBaseChunks { get; }

    DbSet<KnowledgeBaseChunkEmbedding> KnowledgeBaseChunkEmbeddings { get; }

    DbSet<KnowledgeBaseArticleModelPublication> KnowledgeBaseArticleModelPublications { get; }

    /// <summary>Append-only publish snapshots - see KnowledgeBaseArticleVersion's doc comment.</summary>
    DbSet<KnowledgeBaseArticleVersion> KnowledgeBaseArticleVersions { get; }

    DbSet<KnowledgeIngestionJob> KnowledgeIngestionJobs { get; }

    /// <summary>The tenant's append-only audit trail. Written only by AuditTrailSaveChangesInterceptor;
    /// exposed here so it can be read.</summary>
    DbSet<AuditLog> AuditLogs { get; }

    /// <summary>Tenant users, for resolving display names in reports and the audit log. The interface's
    /// usual rule is to reach Identity through UserManager; that is right for creating and changing
    /// users, but a read-only name lookup through a UserManager is only ceremony, and would make these
    /// services untestable without standing up Identity.</summary>
    DbSet<ApplicationUser> Users { get; }

    DbSet<Lead> Leads { get; }

    DbSet<LeadActivity> LeadActivities { get; }

    DbSet<QualificationField> QualificationFields { get; }

    DbSet<LeadQualificationValue> LeadQualificationValues { get; }

    DbSet<LeadScoringRule> LeadScoringRules { get; }

    DbSet<LeadScoreContribution> LeadScoreContributions { get; }

    DbSet<LeadDiscoveryProfile> LeadDiscoveryProfiles { get; }

    DbSet<DiscoveredLead> DiscoveredLeads { get; }

    DbSet<LeadDiscoveryRun> LeadDiscoveryRuns { get; }

    DbSet<Plan> Plans { get; }

    DbSet<Subscription> Subscriptions { get; }

    DbSet<Payment> Payments { get; }

    DbSet<PlanQuota> PlanQuotas { get; }

    DbSet<CreditPack> CreditPacks { get; }

    DbSet<PlanPrice> PlanPrices { get; }

    DbSet<CreditPackPrice> CreditPackPrices { get; }

    DbSet<QuotaGrant> QuotaGrants { get; }

    DbSet<QuotaLedgerEntry> QuotaLedgerEntries { get; }

    DbSet<QuotaWallet> QuotaWallets { get; }

    DbSet<RefundRequest> RefundRequests { get; }

    DbSet<TenantNotification> TenantNotifications { get; }

    DbSet<PlatformAuditLogEntry> PlatformAuditLogEntries { get; }

    DbSet<Announcement> Announcements { get; }

    DbSet<PlatformNotification> PlatformNotifications { get; }

    DbSet<TenantJobSchedule> TenantJobSchedules { get; }

    DbSet<Flowchart> Flowcharts { get; }

    DbSet<FaqEntry> FaqEntries { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops every tracked entity - used to retry a quota operation after a concurrency
    /// conflict, when what was read is stale.</summary>
    void ResetChangeTracker();
}
