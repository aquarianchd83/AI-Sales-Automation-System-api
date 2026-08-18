using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Media;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
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

    DbSet<KnowledgeBaseArticle> KnowledgeBaseArticles { get; }

    DbSet<KnowledgeBaseChunk> KnowledgeBaseChunks { get; }

    DbSet<Lead> Leads { get; }

    DbSet<LeadActivity> LeadActivities { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
