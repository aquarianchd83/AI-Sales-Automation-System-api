using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentStudioKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Context",
                table: "BusinessFacts",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "BusinessFacts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "BusinessFacts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "BusinessFacts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "TenantProvided");

            migrationBuilder.AddColumn<int>(
                name: "ReviewEveryDays",
                table: "BusinessFacts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceResponseId",
                table: "BusinessFacts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "BusinessFacts",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BusinessFactRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Statement = table.Column<string>(type: "nvarchar(max)", maxLength: 4500, nullable: false),
                    Context = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessFactRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessFactRevisions_BusinessFacts_FactId",
                        column: x => x.FactId,
                        principalTable: "BusinessFacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeGapDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    DeferUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeGapDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuestionnaireAnswerRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PreviousJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionnaireAnswerRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestionnaireAnswerRevisions_QuestionnaireResponses_ResponseId",
                        column: x => x.ResponseId,
                        principalTable: "QuestionnaireResponses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessFactRevisions_FactId",
                table: "BusinessFactRevisions",
                column: "FactId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessFactRevisions_TenantId_FactId_Number",
                table: "BusinessFactRevisions",
                columns: new[] { "TenantId", "FactId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeGapDecisions_TenantId_RequirementKey",
                table: "KnowledgeGapDecisions",
                columns: new[] { "TenantId", "RequirementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionnaireAnswerRevisions_ResponseId",
                table: "QuestionnaireAnswerRevisions",
                column: "ResponseId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestionnaireAnswerRevisions_TenantId_ResponseId_CreatedAt",
                table: "QuestionnaireAnswerRevisions",
                columns: new[] { "TenantId", "ResponseId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessFactRevisions");

            migrationBuilder.DropTable(
                name: "KnowledgeGapDecisions");

            migrationBuilder.DropTable(
                name: "QuestionnaireAnswerRevisions");

            migrationBuilder.DropColumn(
                name: "Context",
                table: "BusinessFacts");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "BusinessFacts");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "BusinessFacts");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "BusinessFacts");

            migrationBuilder.DropColumn(
                name: "ReviewEveryDays",
                table: "BusinessFacts");

            migrationBuilder.DropColumn(
                name: "SourceResponseId",
                table: "BusinessFacts");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "BusinessFacts");
        }
    }
}
