using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase6VectorStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "VersionMaxNumeric",
                table: "KnowledgeBaseChunks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VersionMinNumeric",
                table: "KnowledgeBaseChunks",
                type: "bigint",
                nullable: true);

            // -----------------------------------------------------------------------------
            // The native vector column, added ONLY where the server can hold one.
            //
            // VECTOR(1536) arrived in SQL Server 2025. A migration that declared it unconditionally
            // would fail to run at all on anything older - including the developer machines and CI
            // runners this project builds on - so it is created through dynamic SQL guarded by a
            // catalog check. sp_executesql matters here: SQL Server parses a whole batch before
            // executing any of it, so an inline ALTER mentioning VECTOR would be a parse error on an
            // old server even inside an IF that would never have run. Deferring the parse to
            // execution time is the only way the guard actually guards.
            //
            // The column is deliberately absent from the EF model - see SqlServerVectorStore. That
            // keeps one model valid on both kinds of server, and means the fallback store is not
            // carrying a property for a column it will never have.
            // -----------------------------------------------------------------------------
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.types WHERE name = 'vector' AND schema_id = SCHEMA_ID('sys'))
                   AND NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('KnowledgeBaseChunks') AND name = 'EmbeddingVector')
                BEGIN
                    EXEC sp_executesql N'ALTER TABLE [KnowledgeBaseChunks] ADD [EmbeddingVector] VECTOR(1536) NULL';

                    -- Backfills from the JSON column, which every previously embedded chunk already
                    -- has. Without this, upgrading to a 2025 server would leave the native column
                    -- empty and the vector leg returning nothing at all - a knowledge base that
                    -- silently stopped answering rather than one that visibly failed.
                    EXEC sp_executesql N'
                        UPDATE [KnowledgeBaseChunks]
                        SET    [EmbeddingVector] = CAST([Embedding] AS VECTOR(1536))
                        WHERE  [Embedding] IS NOT NULL
                          AND  [EmbeddingDimensions] = 1536';

                    -- DiskANN approximate index. Created after the backfill so it is built over real
                    -- data rather than maintained row by row during it.
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KBChunks_EmbeddingVector')
                        EXEC sp_executesql N'
                            CREATE VECTOR INDEX [IX_KBChunks_EmbeddingVector]
                            ON [KnowledgeBaseChunks]([EmbeddingVector])
                            WITH (METRIC = ''cosine'', TYPE = ''diskann'')';
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VersionMaxNumeric",
                table: "KnowledgeBaseChunks");

            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KBChunks_EmbeddingVector')
                    EXEC sp_executesql N'DROP INDEX [IX_KBChunks_EmbeddingVector] ON [KnowledgeBaseChunks]';

                IF EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID('KnowledgeBaseChunks') AND name = 'EmbeddingVector')
                    EXEC sp_executesql N'ALTER TABLE [KnowledgeBaseChunks] DROP COLUMN [EmbeddingVector]';
            ");

            migrationBuilder.DropColumn(
                name: "VersionMinNumeric",
                table: "KnowledgeBaseChunks");
        }
    }
}
