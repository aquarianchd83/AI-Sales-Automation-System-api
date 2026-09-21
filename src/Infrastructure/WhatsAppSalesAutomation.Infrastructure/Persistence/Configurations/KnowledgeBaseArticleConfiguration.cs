using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseArticleConfiguration : IEntityTypeConfiguration<KnowledgeBaseArticle>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseArticle> builder)
    {
        builder.ToTable("KnowledgeBaseArticles", table =>
        {
            // ── Business rules as database invariants (§J.4) ──────────────────────────────
            // These are not belt-and-braces duplicates of the application checks. The application
            // check protects the good path; the constraint protects every other one - a background
            // job, a seeder, a future endpoint, a hand-run UPDATE during an incident.

            // BR-3: tenant knowledge can never hold platform authority. This constraint IS the rule.
            table.HasCheckConstraint("CK_KBArticles_TenantAuthority",
                "[TenantId] IS NULL OR [AuthorityRank] <= 30");

            // The denormalized scope column and the nullability of TenantId must agree, or every
            // listing that filters on one and every constraint that reads the other disagree.
            table.HasCheckConstraint("CK_KBArticles_ScopeMatchesTenant",
                "([TenantScope] = 'Global' AND [TenantId] IS NULL) OR ([TenantScope] = 'Tenant' AND [TenantId] IS NOT NULL)");

            // An article that stops being true before it starts is not a window, it is a typo.
            table.HasCheckConstraint("CK_KBArticles_EffectiveWindow",
                "[EffectiveTo] IS NULL OR [EffectiveTo] > [EffectiveFrom]");

            // Published without an approver would mean the AI is grounding answers in text nobody
            // signed off - the single most important thing this schema refuses to allow.
            table.HasCheckConstraint("CK_KBArticles_PublishedHasApprover",
                "[Status] <> 'Published' OR ([ApprovedBy] IS NOT NULL AND [ApprovedAt] IS NOT NULL)");

            table.HasCheckConstraint("CK_KBArticles_AuthorityRange", "[AuthorityRank] BETWEEN 0 AND 100");
            table.HasCheckConstraint("CK_KBArticles_PriorityRange", "[Priority] BETWEEN 0 AND 100");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.ArticleKey).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Title).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Content).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(a => a.ContentHash).IsRequired().HasMaxLength(64);   // SHA-256 as lowercase hex
        builder.Property(a => a.Keywords).HasMaxLength(1000);
        builder.Property(a => a.SubCategory).HasMaxLength(100);
        builder.Property(a => a.CountryCode).HasMaxLength(2);
        builder.Property(a => a.LanguageCode).IsRequired().HasMaxLength(10);
        builder.Property(a => a.AppliesToVersionMin).HasMaxLength(20);
        builder.Property(a => a.AppliesToVersionMax).HasMaxLength(20);
        builder.Property(a => a.LifecycleNote).HasMaxLength(1000);

        // Persisted as strings, per the convention used throughout this model - the numeric values
        // then carry no meaning in the data, so a member can be inserted into any of these enums
        // without renumbering existing rows. Lengths are the longest member name plus headroom.
        // CK_KBArticles_ScopeMatchesTenant and CK_KBArticles_PublishedHasApprover above both compare
        // against the string form, so these conversions are load-bearing for those constraints.
        builder.Property(a => a.TenantScope).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(a => a.SourceType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(a => a.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(a => a.Category).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(a => a.ProductModule).HasConversion<string>().HasMaxLength(20);

        // ── Indexes (§J.3, article side) ──────────────────────────────────────────────────

        // Makes "exactly one current version per (scope, key, language)" a database invariant, so a
        // rollback is a two-row flag swap that either succeeds atomically or fails loudly.
        builder.HasIndex(a => new { a.TenantId, a.ArticleKey, a.LanguageCode })
            .IsUnique()
            .HasDatabaseName("UX_KBArticles_CurrentVersion")
            .HasFilter("[IsCurrentVersion] = 1 AND [IsDeleted] = 0");

        // The expiry and review-reminder jobs run on this.
        builder.HasIndex(a => new { a.Status, a.ReviewDueAt, a.EffectiveTo })
            .HasDatabaseName("IX_KBArticles_Lifecycle")
            .HasFilter("[IsDeleted] = 0");

        // Exact-duplicate detection on save.
        builder.HasIndex(a => new { a.TenantId, a.ContentHash })
            .HasDatabaseName("IX_KBArticles_Dedup")
            .HasFilter("[IsCurrentVersion] = 1 AND [IsDeleted] = 0");

        builder.HasMany(a => a.Chunks)
            .WithOne()
            .HasForeignKey(c => c.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.ModelPublications)
            .WithOne()
            .HasForeignKey(p => p.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        // No HasQueryFilter here - see CustomerConfiguration's equivalent comment: the combined
        // (!IsDeleted && (TenantId IS NULL OR TenantId == ...)) filter is built reflectively in
        // ApplicationDbContext.OnModelCreating instead, since EF Core allows only one per entity.
        // This entity takes the ITenantScopedOrGlobal branch of that pass, not the ITenantOwned one.
    }
}
