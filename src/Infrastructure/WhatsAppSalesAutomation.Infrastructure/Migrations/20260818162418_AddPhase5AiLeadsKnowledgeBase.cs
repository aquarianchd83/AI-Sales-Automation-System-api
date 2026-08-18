using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase5AiLeadsKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AiConfidenceLast",
                table: "Conversations",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastDetectedIntent",
                table: "Conversations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastLeadScore",
                table: "Conversations",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Conversations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiInteractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InboundMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DetectedIntent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConfidenceScore = table.Column<double>(type: "float", nullable: false),
                    ExtractedEntitiesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProposedResponseText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActionTaken = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ModelUsed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    CompletionTokens = table.Column<int>(type: "int", nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInteractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiInteractions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiInteractions_Messages_InboundMessageId",
                        column: x => x.InboundMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeBaseArticles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBaseArticles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Score = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ScoreNumeric = table.Column<int>(type: "int", nullable: false),
                    Budget = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Interest = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PurchaseTimeline = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedTo = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Leads_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeBaseChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    ChunkText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Embedding = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokenCount = table.Column<int>(type: "int", nullable: false),
                    EmbeddedFromArticleVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBaseChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeBaseChunks_KnowledgeBaseArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KnowledgeBaseArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeadActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadActivities_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiInteractionSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiInteractionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KnowledgeBaseChunkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelevanceScore = table.Column<double>(type: "float", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInteractionSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiInteractionSources_AiInteractions_AiInteractionId",
                        column: x => x.AiInteractionId,
                        principalTable: "AiInteractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiInteractionSources_KnowledgeBaseChunks_KnowledgeBaseChunkId",
                        column: x => x.KnowledgeBaseChunkId,
                        principalTable: "KnowledgeBaseChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_ConversationId",
                table: "AiInteractions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_InboundMessageId",
                table: "AiInteractions",
                column: "InboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractionSources_AiInteractionId",
                table: "AiInteractionSources",
                column: "AiInteractionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractionSources_KnowledgeBaseChunkId",
                table: "AiInteractionSources",
                column: "KnowledgeBaseChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseArticles_Status",
                table: "KnowledgeBaseArticles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseChunks_ArticleId",
                table: "KnowledgeBaseChunks",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_LeadId",
                table: "LeadActivities",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_CustomerId",
                table: "Leads",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Stage",
                table: "Leads",
                column: "Stage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiInteractionSources");

            migrationBuilder.DropTable(
                name: "LeadActivities");

            migrationBuilder.DropTable(
                name: "AiInteractions");

            migrationBuilder.DropTable(
                name: "KnowledgeBaseChunks");

            migrationBuilder.DropTable(
                name: "Leads");

            migrationBuilder.DropTable(
                name: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "AiConfidenceLast",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastDetectedIntent",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastLeadScore",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Conversations");
        }
    }
}
