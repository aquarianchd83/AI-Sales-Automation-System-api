using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Media;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Entities.Webhooks;
using WhatsAppSalesAutomation.Infrastructure.Settings;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IApplicationDbContext
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUserService;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ITenantContext tenantContext,
        ICurrentUserService currentUserService) : base(options)
    {
        _tenantContext = tenantContext;
        _currentUserService = currentUserService;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

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

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    // Deliberately not on IApplicationDbContext - see WhatsAppAccessTokenState's own doc comment for
    // why this is Infrastructure-internal state, not something Application services should reach.
    public DbSet<WhatsAppAccessTokenState> WhatsAppAccessTokenStates => Set<WhatsAppAccessTokenState>();

    // Deliberately not on IApplicationDbContext - reached only through IAppSettingsStore/
    // ISettingsService, same reasoning as WhatsAppAccessTokenStates above.
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    // Deliberately not on IApplicationDbContext - reached only through ITenantWhatsAppConfigProvider/
    // ITenantAiConfigProvider, same reasoning as AppSettings/WhatsAppAccessTokenStates above. Both are
    // still ITenantOwned (see each entity's own doc comment), so the reflective filter pass below
    // picks them up like any other tenant-owned entity despite not appearing on the interface.
    public DbSet<TenantWhatsAppConfig> TenantWhatsAppConfigs => Set<TenantWhatsAppConfig>();

    public DbSet<TenantAiProviderConfig> TenantAiProviderConfigs => Set<TenantAiProviderConfig>();

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

        ApplyTenantQueryFilters(builder);
    }

    /// <summary>
    /// Applies a combined tenant + (where applicable) soft-delete global query filter to every entity
    /// in the model that implements <see cref="ITenantOwned"/>, plus the <see cref="ApplicationUser"/>
    /// special case (nullable TenantId, not ITenantOwned - see its own doc comment for why). EF Core
    /// allows only one <c>HasQueryFilter</c> call per entity, and the set of tenant-owned entities is
    /// only known at runtime by reflecting over the model, so this can't be written as a plain
    /// per-entity <c>HasQueryFilter</c> call the way <c>CustomerConfiguration</c> used to before
    /// tenancy existed - see <see cref="ITenantOwned"/>'s own doc comment.
    ///
    /// A <c>PlatformSuperAdmin</c> request has <c>_tenantContext.TenantId == null</c>; comparing that
    /// against a non-nullable <c>TenantId</c> column always evaluates to false, so such a request sees
    /// zero rows through this filter by design (platform-wide listings are separate, explicit,
    /// <c>IgnoreQueryFilters()</c> endpoints, not an implicit "see everything" here).
    ///
    /// The <see cref="ApplicationUser"/> filter additionally short-circuits to "no restriction" when
    /// <see cref="ICurrentUserService.UserId"/> is null, i.e. there is no authenticated principal at
    /// all - <c>AuthService</c>'s login, signup (duplicate-email check) and refresh-token flows all
    /// look a user up by email/id through <c>UserManager</c> before any tenant is known (that is the
    /// entire point of login), so without this carve-out every one of those lookups would silently
    /// find nothing and login would fail for every tenant user. This does not weaken isolation:
    /// nothing anonymous ever *lists* users, it only ever matches one by an already-unique
    /// email/id, which is the normal shape of a login/signup flow.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder builder)
    {
        var setFilterMethod = typeof(ApplicationDbContext)
            .GetMethod(nameof(SetTenantOwnedFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (!typeof(ITenantOwned).IsAssignableFrom(clrType))
                continue;

            setFilterMethod.MakeGenericMethod(clrType).Invoke(this, new object[] { builder });
        }

        builder.Entity<ApplicationUser>().HasQueryFilter(u =>
            _currentUserService.UserId == null || u.TenantId == _tenantContext.TenantId);
    }

    private void SetTenantOwnedFilter<TEntity>(ModelBuilder builder) where TEntity : class, ITenantOwned
    {
        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            builder.Entity<TEntity>().HasQueryFilter(e =>
                e.TenantId == _tenantContext.TenantId && !EF.Property<bool>(e, nameof(ISoftDelete.IsDeleted)));
        }
        else
        {
            builder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        }
    }
}
