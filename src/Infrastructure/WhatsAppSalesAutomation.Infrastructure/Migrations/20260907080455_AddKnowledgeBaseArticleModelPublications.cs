using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeBaseArticleModelPublications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeBaseArticleModelPublications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBaseArticleModelPublications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeBaseArticleModelPublications_KnowledgeBaseArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KnowledgeBaseArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseArticleModelPublications_ArticleId_Provider",
                table: "KnowledgeBaseArticleModelPublications",
                columns: new[] { "ArticleId", "Provider" },
                unique: true);

            // Backward-compat backfill: before this migration, "Published" meant "retrievable by
            // whichever chat provider happens to be active" - there was no per-model concept at all.
            // Give every already-Published article a row for all three providers so it stays
            // retrievable everywhere it already was, until someone explicitly narrows it via the new
            // publish/unpublish-to-model endpoints. New articles published after this point start
            // with zero rows (must be explicitly published to a model) - see
            // KnowledgeBaseArticleModelPublication's doc comment.
            migrationBuilder.Sql(@"
                INSERT INTO [KnowledgeBaseArticleModelPublications] ([Id], [ArticleId], [Provider], [PublishedAt], [PublishedBy], [CreatedAt], [UpdatedAt])
                SELECT NEWID(), a.[Id], p.[Provider], COALESCE(a.[UpdatedAt], a.[CreatedAt]), NULL, COALESCE(a.[UpdatedAt], a.[CreatedAt]), NULL
                FROM [KnowledgeBaseArticles] a
                CROSS JOIN (VALUES ('OpenAI'), ('Google'), ('Anthropic')) AS p([Provider])
                WHERE a.[Status] = 'Published';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeBaseArticleModelPublications");
        }
    }
}
