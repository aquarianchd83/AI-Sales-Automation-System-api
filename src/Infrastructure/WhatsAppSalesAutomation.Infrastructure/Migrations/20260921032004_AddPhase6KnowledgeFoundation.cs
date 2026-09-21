using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase6KnowledgeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KnowledgeBaseChunks_ArticleId",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeBaseArticles_Status",
                table: "KnowledgeBaseArticles");

            migrationBuilder.RenameColumn(
                name: "Version",
                table: "KnowledgeBaseArticles",
                newName: "VersionNumber");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseChunks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "ArticleStatus",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "AtomicGroupId",
                table: "KnowledgeBaseChunks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AtomicGroupSequence",
                table: "KnowledgeBaseChunks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AtomicGroupTotal",
                table: "KnowledgeBaseChunks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AuthorityRank",
                table: "KnowledgeBaseChunks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContextHeader",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveFrom",
                table: "KnowledgeBaseChunks",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveTo",
                table: "KnowledgeBaseChunks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimensions",
                table: "KnowledgeBaseChunks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingInput",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "KnowledgeBaseChunks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentArticleVersion",
                table: "KnowledgeBaseChunks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LanguageCode",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProductModule",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "KnowledgeBaseChunks",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseChunkEmbeddings",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseArticles",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "SourceType",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<string>(
                name: "AppliesToVersionMax",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliesToVersionMin",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "KnowledgeBaseArticles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArticleKey",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AuthorityRank",
                table: "KnowledgeBaseArticles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveFrom",
                table: "KnowledgeBaseArticles",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveTo",
                table: "KnowledgeBaseArticles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentVersion",
                table: "KnowledgeBaseArticles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LanguageCode",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdatedAt",
                table: "KnowledgeBaseArticles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedBy",
                table: "KnowledgeBaseArticles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleNote",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "KnowledgeBaseArticles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "KnowledgeBaseArticles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProductModule",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "KnowledgeBaseArticles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedBy",
                table: "KnowledgeBaseArticles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewDueAt",
                table: "KnowledgeBaseArticles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubCategory",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesArticleId",
                table: "KnowledgeBaseArticles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantScope",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseArticleModelPublications",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            // -----------------------------------------------------------------------------
            // Backfill, between adding the columns and constraining them.
            //
            // Every new column above landed with a CLR default - empty string, 0, false, year 1 -
            // and several of those defaults are values the check constraints and indexes below
            // would reject, or that EF Core cannot map back to an enum on read. On an empty
            // database none of this matters; on one with articles in it, skipping this step is the
            // difference between a migration and an outage. This is the M3/M4 split from the
            // Phase 6 document: add nullable, backfill, then constrain.
            // -----------------------------------------------------------------------------

            // The old free-text Category has no home in the KnowledgeCategory enum, and discarding
            // it would throw away the only classification these articles have. SubCategory is free
            // text for exactly this reason, so the author's own wording moves there and Category
            // takes a valid enum member. This must precede the AlterColumn below, which narrows
            // the column to nvarchar(20).
            migrationBuilder.Sql(@"
                UPDATE [KnowledgeBaseArticles]
                SET [SubCategory] = LEFT([Category], 100)
                WHERE [Category] IS NOT NULL AND LTRIM(RTRIM([Category])) <> '' AND [SubCategory] IS NULL;

                UPDATE [KnowledgeBaseArticles]
                SET [Category] = CASE
                        WHEN [Category] IN (
                            'GettingStarted','Billing','Subscription','AiUsage','WhatsApp','Campaigns',
                            'LeadDiscovery','Conversations','Crm','Templates','Integrations','Security',
                            'Policy','Troubleshooting','ReleaseNotes') THEN [Category]
                        ELSE 'GettingStarted'
                    END;
            ");

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.Sql(@"
                -- SourceType recorded how content ARRIVED (Manual/Upload); it now records how much
                -- the content should be TRUSTED. Neither old value is a member of the new enum, so
                -- leaving them would make every existing article unreadable by EF Core. Both map to
                -- AdminConfiguredArticle, the one source type a tenant is allowed to author.
                UPDATE [KnowledgeBaseArticles]
                SET [SourceType] = 'AdminConfiguredArticle'
                WHERE [SourceType] IN ('Manual', 'Upload') OR [SourceType] = '';

                -- Everything that existed before this migration was tenant-owned; GLOBAL knowledge
                -- did not exist yet. CK_KBArticles_ScopeMatchesTenant rejects the empty-string
                -- default, so this is not cosmetic.
                UPDATE [KnowledgeBaseArticles] SET [TenantScope] = 'Tenant' WHERE [TenantScope] = '';

                -- AdminConfiguredArticle ranks 30, which is also the tenant ceiling that
                -- CK_KBArticles_TenantAuthority enforces.
                UPDATE [KnowledgeBaseArticles] SET [AuthorityRank] = 30 WHERE [AuthorityRank] = 0;
                UPDATE [KnowledgeBaseArticles] SET [Priority] = 50 WHERE [Priority] = 0;
                UPDATE [KnowledgeBaseArticles] SET [LanguageCode] = 'en' WHERE [LanguageCode] = '';

                -- A slug of the title plus twelve hex characters of the row's own id. The suffix is
                -- what makes it unique without a collision loop, which matters because
                -- UX_KBArticles_CurrentVersion is created below and would otherwise fail the whole
                -- migration on two articles that happen to share a title.
                UPDATE [KnowledgeBaseArticles]
                SET [ArticleKey] = LEFT(LOWER(REPLACE(REPLACE(LTRIM(RTRIM([Title])), ' ', '-'), '/', '-')), 160)
                                   + '-' + LOWER(RIGHT(CONVERT(varchar(36), [Id]), 12))
                WHERE [ArticleKey] = '';

                -- Each existing article is the only version of itself, so it is the current one.
                -- The bit column defaulted to 0, which would have made every article invisible to a
                -- retrieval filter that requires IsCurrentVersion - a silently empty knowledge base.
                UPDATE [KnowledgeBaseArticles] SET [IsCurrentVersion] = 1;

                -- Year 1 is a valid effective-from, but CreatedAt is the truthful one.
                UPDATE [KnowledgeBaseArticles]
                SET [EffectiveFrom] = [CreatedAt]
                WHERE [EffectiveFrom] = '0001-01-01';

                -- CK_KBArticles_PublishedHasApprover requires BOTH ApprovedBy and ApprovedAt on a
                -- Published row. ApprovedAt is a brand-new column and is therefore NULL on every
                -- existing Published article, so without this the constraint below cannot be added
                -- at all. UpdatedAt/CreatedAt is the closest honest approximation of when approval
                -- happened.
                UPDATE [KnowledgeBaseArticles]
                SET [ApprovedAt] = COALESCE([UpdatedAt], [CreatedAt])
                WHERE [Status] = 'Published' AND [ApprovedBy] IS NOT NULL AND [ApprovedAt] IS NULL;

                -- An article with no recorded approver is moved back to Draft rather than being
                -- given a fabricated one. The constraint would reject it either way; inventing an
                -- approver to satisfy a constraint about human sign-off would defeat the constraint.
                UPDATE [KnowledgeBaseArticles]
                SET [Status] = 'Draft',
                    [LifecycleNote] = 'Unpublished by the Phase 6 migration: no approver was recorded.'
                WHERE [Status] = 'Published' AND [ApprovedBy] IS NULL;

                UPDATE [KnowledgeBaseArticles]
                SET [PublishedAt] = COALESCE([PublishedAt], [ApprovedAt]),
                    [PublishedBy] = COALESCE([PublishedBy], [ApprovedBy])
                WHERE [Status] = 'Published';
            ");

            migrationBuilder.Sql(@"
                -- The chunk side of the same problem. These columns are denormalized from the
                -- article, so the backfill is a join rather than a constant - and IsActive
                -- defaulting to 0 would have taken every existing chunk out of retrieval, the same
                -- silently-empty-knowledge-base failure as IsCurrentVersion above.
                UPDATE c
                SET c.[ArticleStatus]           = a.[Status],
                    c.[SourceType]              = a.[SourceType],
                    c.[AuthorityRank]           = a.[AuthorityRank],
                    c.[LanguageCode]            = a.[LanguageCode],
                    c.[CountryCode]             = a.[CountryCode],
                    c.[ProductModule]           = a.[ProductModule],
                    c.[EffectiveFrom]           = a.[EffectiveFrom],
                    c.[EffectiveTo]             = a.[EffectiveTo],
                    c.[IsCurrentArticleVersion] = a.[IsCurrentVersion],
                    c.[IsActive]                = 1,
                    -- ContextHeader and EmbeddingInput are deliberately NOT reconstructed in the
                    -- format the Phase 6 chunker uses. EmbeddingInput must equal what was actually
                    -- embedded, and these vectors were built from the bare chunk text - writing a
                    -- header here would claim the stored vector covers text it never saw. The next
                    -- re-index fixes both properly. SearchText is safe to build now because nothing
                    -- has been indexed from it yet.
                    c.[ContextHeader]           = '',
                    c.[EmbeddingInput]          = c.[ChunkText],
                    c.[SearchText]              = c.[ChunkText]
                                                  + COALESCE(' ' + a.[Keywords], '')
                                                  + COALESCE(' ' + a.[Keywords], '')
                FROM [KnowledgeBaseChunks] c
                INNER JOIN [KnowledgeBaseArticles] a ON a.[Id] = c.[ArticleId];
            ");

            migrationBuilder.CreateTable(
                name: "KnowledgeBaseArticleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBaseArticleVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KBChunks_Article",
                table: "KnowledgeBaseChunks",
                columns: new[] { "ArticleId", "ChunkIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_KBChunks_AtomicGroup",
                table: "KnowledgeBaseChunks",
                columns: new[] { "AtomicGroupId", "AtomicGroupSequence" },
                filter: "[AtomicGroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KBChunks_Retrieval",
                table: "KnowledgeBaseChunks",
                columns: new[] { "TenantId", "ArticleStatus", "IsActive", "IsCurrentArticleVersion", "LanguageCode" },
                filter: "[IsActive] = 1")
                .Annotation("SqlServer:Include", new[] { "ArticleId", "AuthorityRank", "ProductModule", "CountryCode", "EffectiveFrom", "EffectiveTo", "AtomicGroupId", "ChunkIndex" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBChunks_AtomicGroupComplete",
                table: "KnowledgeBaseChunks",
                sql: "([AtomicGroupId] IS NULL AND [AtomicGroupSequence] IS NULL AND [AtomicGroupTotal] IS NULL) OR ([AtomicGroupId] IS NOT NULL AND [AtomicGroupSequence] IS NOT NULL AND [AtomicGroupTotal] IS NOT NULL AND [AtomicGroupSequence] BETWEEN 1 AND [AtomicGroupTotal])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBChunks_AuthorityRange",
                table: "KnowledgeBaseChunks",
                sql: "[AuthorityRank] BETWEEN 0 AND 100");

            migrationBuilder.CreateIndex(
                name: "IX_KBArticles_Dedup",
                table: "KnowledgeBaseArticles",
                columns: new[] { "TenantId", "ContentHash" },
                filter: "[IsCurrentVersion] = 1 AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_KBArticles_Lifecycle",
                table: "KnowledgeBaseArticles",
                columns: new[] { "Status", "ReviewDueAt", "EffectiveTo" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_KBArticles_CurrentVersion",
                table: "KnowledgeBaseArticles",
                columns: new[] { "TenantId", "ArticleKey", "LanguageCode" },
                unique: true,
                filter: "[IsCurrentVersion] = 1 AND [IsDeleted] = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBArticles_AuthorityRange",
                table: "KnowledgeBaseArticles",
                sql: "[AuthorityRank] BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBArticles_EffectiveWindow",
                table: "KnowledgeBaseArticles",
                sql: "[EffectiveTo] IS NULL OR [EffectiveTo] > [EffectiveFrom]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBArticles_PriorityRange",
                table: "KnowledgeBaseArticles",
                sql: "[Priority] BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBArticles_PublishedHasApprover",
                table: "KnowledgeBaseArticles",
                sql: "[Status] <> 'Published' OR ([ApprovedBy] IS NOT NULL AND [ApprovedAt] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBArticles_ScopeMatchesTenant",
                table: "KnowledgeBaseArticles",
                sql: "([TenantScope] = 'Global' AND [TenantId] IS NULL) OR ([TenantScope] = 'Tenant' AND [TenantId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KBArticles_TenantAuthority",
                table: "KnowledgeBaseArticles",
                sql: "[TenantId] IS NULL OR [AuthorityRank] <= 30");

            migrationBuilder.CreateIndex(
                name: "IX_KBArticleVersions_Article",
                table: "KnowledgeBaseArticleVersions",
                columns: new[] { "ArticleId", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "UX_KBArticleVersions_KeyVersion",
                table: "KnowledgeBaseArticleVersions",
                columns: new[] { "TenantId", "ArticleKey", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeBaseArticleVersions");

            migrationBuilder.DropIndex(
                name: "IX_KBChunks_Article",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropIndex(
                name: "IX_KBChunks_AtomicGroup",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropIndex(
                name: "IX_KBChunks_Retrieval",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBChunks_AtomicGroupComplete",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBChunks_AuthorityRange",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropIndex(
                name: "IX_KBArticles_Dedup",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropIndex(
                name: "IX_KBArticles_Lifecycle",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropIndex(
                name: "UX_KBArticles_CurrentVersion",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBArticles_AuthorityRange",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBArticles_EffectiveWindow",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBArticles_PriorityRange",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBArticles_PublishedHasApprover",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBArticles_ScopeMatchesTenant",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KBArticles_TenantAuthority",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "ArticleStatus",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "AtomicGroupId",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "AtomicGroupSequence",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "AtomicGroupTotal",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "AuthorityRank",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "ContextHeader",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingDimensions",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingInput",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "IsCurrentArticleVersion",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "LanguageCode",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "ProductModule",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "AppliesToVersionMax",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "AppliesToVersionMin",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "ArticleKey",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "AuthorityRank",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "IsCurrentVersion",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "LanguageCode",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedAt",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedBy",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "LifecycleNote",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "ProductModule",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "PublishedBy",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "ReviewDueAt",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "SubCategory",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "SupersedesArticleId",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "TenantScope",
                table: "KnowledgeBaseArticles");

            migrationBuilder.RenameColumn(
                name: "VersionNumber",
                table: "KnowledgeBaseArticles",
                newName: "Version");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseChunks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseChunkEmbeddings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseArticles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "SourceType",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "KnowledgeBaseArticleModelPublications",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseChunks_ArticleId",
                table: "KnowledgeBaseChunks",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseArticles_Status",
                table: "KnowledgeBaseArticles",
                column: "Status");
        }
    }
}
