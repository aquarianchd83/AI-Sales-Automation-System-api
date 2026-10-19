using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureBenefits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunVersion = table.Column<int>(type: "int", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Benefit = table.Column<string>(type: "nvarchar(1500)", maxLength: 1500, nullable: true),
                    Segment = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Confidence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HasUnsupportedClaim = table.Column<bool>(type: "bit", nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsSimulated = table.Column<bool>(type: "bit", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureBenefits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Fact = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ChosenValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDraftRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Total = table.Column<int>(type: "int", nullable: false),
                    Done = table.Column<int>(type: "int", nullable: false),
                    Drafted = table.Column<int>(type: "int", nullable: false),
                    NoEvidence = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDraftRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsStandard = table.Column<bool>(type: "bit", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ClarifyingQuestion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeQuestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Confidence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Limitation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ClarifyingQuestion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsMaterialClaim = table.Column<bool>(type: "bit", nullable: false),
                    HasUnsupportedClaim = table.Column<bool>(type: "bit", nullable: false),
                    HasConflict = table.Column<bool>(type: "bit", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsSimulated = table.Column<bool>(type: "bit", nullable: false),
                    AiCorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfidenceReasons = table.Column<string>(type: "nvarchar(1500)", maxLength: 1500, nullable: true),
                    SourceVersions = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeAnswers_KnowledgeQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "KnowledgeQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnswerEvidences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PassageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceVersion = table.Column<int>(type: "int", nullable: false),
                    Excerpt = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Heading = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PassageHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerEvidences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnswerEvidences_KnowledgeAnswers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "KnowledgeAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerEvidences_AnswerId",
                table: "AnswerEvidences",
                column: "AnswerId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerEvidences_TenantId_AnswerId",
                table: "AnswerEvidences",
                columns: new[] { "TenantId", "AnswerId" });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerEvidences_TenantId_SourceId",
                table: "AnswerEvidences",
                columns: new[] { "TenantId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureBenefits_TenantId_RunVersion",
                table: "FeatureBenefits",
                columns: new[] { "TenantId", "RunVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeAnswers_QuestionId",
                table: "KnowledgeAnswers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeAnswers_TenantId_QuestionId_IsCurrent",
                table: "KnowledgeAnswers",
                columns: new[] { "TenantId", "QuestionId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeConflicts_TenantId_AnswerId",
                table: "KnowledgeConflicts",
                columns: new[] { "TenantId", "AnswerId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDraftRuns_TenantId_CreatedAt",
                table: "KnowledgeDraftRuns",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeQuestions_TenantId_Key",
                table: "KnowledgeQuestions",
                columns: new[] { "TenantId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeQuestions_TenantId_SortOrder",
                table: "KnowledgeQuestions",
                columns: new[] { "TenantId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnswerEvidences");

            migrationBuilder.DropTable(
                name: "FeatureBenefits");

            migrationBuilder.DropTable(
                name: "KnowledgeConflicts");

            migrationBuilder.DropTable(
                name: "KnowledgeDraftRuns");

            migrationBuilder.DropTable(
                name: "KnowledgeAnswers");

            migrationBuilder.DropTable(
                name: "KnowledgeQuestions");
        }
    }
}
