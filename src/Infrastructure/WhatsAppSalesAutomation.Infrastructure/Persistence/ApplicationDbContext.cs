using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
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
using WhatsAppSalesAutomation.Infrastructure.Settings;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerTag> CustomerTags => Set<CustomerTag>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignStep> CampaignSteps => Set<CampaignStep>();

    public DbSet<CampaignStepMedia> CampaignStepMedia => Set<CampaignStepMedia>();

    public DbSet<CampaignCustomer> CampaignCustomers => Set<CampaignCustomer>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<HumanHandoff> HumanHandoffs => Set<HumanHandoff>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<AiInteraction> AiInteractions => Set<AiInteraction>();

    public DbSet<AiInteractionSource> AiInteractionSources => Set<AiInteractionSource>();

    public DbSet<KnowledgeBaseArticle> KnowledgeBaseArticles => Set<KnowledgeBaseArticle>();

    public DbSet<KnowledgeBaseChunk> KnowledgeBaseChunks => Set<KnowledgeBaseChunk>();

    public DbSet<KnowledgeBaseChunkEmbedding> KnowledgeBaseChunkEmbeddings => Set<KnowledgeBaseChunkEmbedding>();

    public DbSet<KnowledgeBaseArticleModelPublication> KnowledgeBaseArticleModelPublications => Set<KnowledgeBaseArticleModelPublication>();

    public DbSet<Lead> Leads => Set<Lead>();

    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();

    // Deliberately not on IApplicationDbContext - see WhatsAppAccessTokenState's own doc comment for
    // why this is Infrastructure-internal state, not something Application services should reach.
    public DbSet<WhatsAppAccessTokenState> WhatsAppAccessTokenStates => Set<WhatsAppAccessTokenState>();

    // Deliberately not on IApplicationDbContext - reached only through IAppSettingsStore/
    // ISettingsService, same reasoning as WhatsAppAccessTokenStates above.
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Rename Identity's default AspNet* tables to match the design doc's naming.
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<ApplicationRole>().ToTable("Roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("UserRoles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("RoleClaims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens");
    }
}
