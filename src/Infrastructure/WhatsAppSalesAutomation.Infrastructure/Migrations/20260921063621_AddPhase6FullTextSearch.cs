using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase6FullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Full-Text Search for the keyword half of hybrid retrieval - created ONLY where the
            // feature is installed. On a server without it (this project's developer machines and CI
            // among them) this is a no-op, and retrieval falls back to in-application BM25; see
            // SqlServerFullTextCapability, which decides which path is live by probing at startup.
            //
            // suppressTransaction is required, not tidy: SQL Server refuses CREATE FULLTEXT CATALOG
            // and CREATE FULLTEXT INDEX inside a user transaction, and EF wraps every migration in one.
            // The statements are idempotent for exactly that reason - without the transaction there
            // is no rollback if a later step fails, so re-running must be safe.
            //
            // sp_executesql defers parsing to execution time, so the FULLTEXT syntax is never parsed
            // on a server that lacks the feature.
            migrationBuilder.Sql(@"
                IF CAST(ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0) AS int) = 1
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'KnowledgeBaseCatalog')
                        EXEC sp_executesql N'CREATE FULLTEXT CATALOG [KnowledgeBaseCatalog]';

                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('KnowledgeBaseChunks'))
                        EXEC sp_executesql N'
                            CREATE FULLTEXT INDEX ON [KnowledgeBaseChunks] ([SearchText] LANGUAGE 1033)
                            KEY INDEX [PK_KnowledgeBaseChunks]
                            ON [KnowledgeBaseCatalog]
                            WITH (CHANGE_TRACKING = AUTO, STOPLIST = SYSTEM)';
                END
            ", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('KnowledgeBaseChunks'))
                    EXEC sp_executesql N'DROP FULLTEXT INDEX ON [KnowledgeBaseChunks]';

                IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'KnowledgeBaseCatalog')
                    EXEC sp_executesql N'DROP FULLTEXT CATALOG [KnowledgeBaseCatalog]';
            ", suppressTransaction: true);
        }
    }
}
