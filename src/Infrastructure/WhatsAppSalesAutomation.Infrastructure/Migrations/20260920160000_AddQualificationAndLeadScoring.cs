using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQualificationAndLeadScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Tenant: business context the AI sales agent needs, plus its conversation goal ──────
            migrationBuilder.AddColumn<string>(
                name: "BusinessLocation",
                table: "Tenants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingHours",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiConversationGoal",
                table: "Tenants",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Enquiry");

            migrationBuilder.AddColumn<bool>(
                name: "AiMayDiscloseLeadScore",
                table: "Tenants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // ── Customer: how an opt-out was detected ────────────────────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "OptOutSource",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            // ── Lead: intent and hot-lead state ──────────────────────────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "CurrentIntent",
                table: "Leads",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HotLeadDetectedAt",
                table: "Leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HotLeadReason",
                table: "Leads",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // ── HumanHandoff: the structured briefing for whoever takes over ─────────────────────
            migrationBuilder.AddColumn<string>(
                name: "SummaryJson",
                table: "HumanHandoffs",
                type: "nvarchar(max)",
                nullable: true);

            // ── AiInteraction: what the turn captured, what it asked, what the model reported ────
            migrationBuilder.AddColumn<string>(
                name: "CapturedFieldKeysJson",
                table: "AiInteractions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AskedFieldKey",
                table: "AiInteractions",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BuyingIntentReported",
                table: "AiInteractions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HumanRequestReported",
                table: "AiInteractions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OptOutReported",
                table: "AiInteractions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // ── QualificationFields: the tenant's qualification schema ───────────────────────────
            migrationBuilder.CreateTable(
                name: "QualificationFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Question = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ScoreWeight = table.Column<int>(type: "int", nullable: false),
                    AllowedValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationPattern = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    LastUpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualificationFields", x => x.Id);
                });

            // ── LeadScoringRules: what each signal is worth, per tenant ──────────────────────────
            migrationBuilder.CreateTable(
                name: "LeadScoringRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RuleType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MatchValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    OncePerLead = table.Column<bool>(type: "bit", nullable: false),
                    MarksLeadHot = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    LastUpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadScoringRules", x => x.Id);
                });

            // ── LeadQualificationValues: what each lead actually told us ─────────────────────────
            migrationBuilder.CreateTable(
                name: "LeadQualificationValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RawValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NormalizedValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CapturedFromMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CapturedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExtractionConfidence = table.Column<double>(type: "float", nullable: false),
                    IsSuperseded = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadQualificationValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadQualificationValues_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    // Restrict, not Cascade: deleting a field the AI has already captured answers for
                    // should fail loudly rather than silently erase what customers told us.
                    table.ForeignKey(
                        name: "FK_LeadQualificationValues_QualificationFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "QualificationFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // ── LeadScoreContributions: why a lead scores what it scores ─────────────────────────
            migrationBuilder.CreateTable(
                name: "LeadScoreContributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    IsOnce = table.Column<bool>(type: "bit", nullable: false),
                    TriggeredByInteractionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadScoreContributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadScoreContributions_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    // SetNull: a deleted rule must not block its own deletion, and SourceKey/DisplayName
                    // are denormalized precisely so the breakdown survives the rule going away.
                    table.ForeignKey(
                        name: "FK_LeadScoreContributions_LeadScoringRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "LeadScoringRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // ── Indexes ──────────────────────────────────────────────────────────────────────────

            // One live field per key per tenant - the key is what the model returns, so two rows
            // answering to it would make a captured value ambiguous.
            migrationBuilder.CreateIndex(
                name: "IX_QualificationFields_TenantId_FieldKey",
                table: "QualificationFields",
                columns: new[] { "TenantId", "FieldKey" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_QualificationFields_TenantId_IsActive_Priority_SortOrder",
                table: "QualificationFields",
                columns: new[] { "TenantId", "IsActive", "Priority", "SortOrder" },
                filter: "[IsDeleted] = 0");

            // At most one current value per (lead, field). Supersede-then-insert is the only way to
            // change a value, so this is what stops a partially-applied capture leaving two.
            migrationBuilder.CreateIndex(
                name: "IX_LeadQualificationValues_LeadId_FieldKey",
                table: "LeadQualificationValues",
                columns: new[] { "LeadId", "FieldKey" },
                unique: true,
                filter: "[IsSuperseded] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LeadQualificationValues_LeadId_IsSuperseded",
                table: "LeadQualificationValues",
                columns: new[] { "LeadId", "IsSuperseded" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadQualificationValues_FieldId",
                table: "LeadQualificationValues",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadScoringRules_TenantId_RuleKey",
                table: "LeadScoringRules",
                columns: new[] { "TenantId", "RuleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadScoringRules_TenantId_IsActive_SortOrder",
                table: "LeadScoringRules",
                columns: new[] { "TenantId", "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadScoreContributions_LeadId_AppliedAt",
                table: "LeadScoreContributions",
                columns: new[] { "LeadId", "AppliedAt" });

            // Enforces LeadScoringRule.OncePerLead at the database, filtered to the contributions that
            // claim to be once-only - a repeatable rule writes IsOnce = 0 rows, which this ignores.
            migrationBuilder.CreateIndex(
                name: "IX_LeadScoreContributions_LeadId_SourceKey",
                table: "LeadScoreContributions",
                columns: new[] { "LeadId", "SourceKey" },
                unique: true,
                filter: "[IsOnce] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LeadScoreContributions_RuleId",
                table: "LeadScoreContributions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_HotLeadDetectedAt",
                table: "Leads",
                columns: new[] { "TenantId", "HotLeadDetectedAt" },
                filter: "[HotLeadDetectedAt] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadQualificationValues");

            migrationBuilder.DropTable(
                name: "LeadScoreContributions");

            migrationBuilder.DropTable(
                name: "QualificationFields");

            migrationBuilder.DropTable(
                name: "LeadScoringRules");

            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_HotLeadDetectedAt",
                table: "Leads");

            migrationBuilder.DropColumn(name: "BusinessLocation", table: "Tenants");
            migrationBuilder.DropColumn(name: "WorkingHours", table: "Tenants");
            migrationBuilder.DropColumn(name: "AiConversationGoal", table: "Tenants");
            migrationBuilder.DropColumn(name: "AiMayDiscloseLeadScore", table: "Tenants");

            migrationBuilder.DropColumn(name: "OptOutSource", table: "Customers");

            migrationBuilder.DropColumn(name: "CurrentIntent", table: "Leads");
            migrationBuilder.DropColumn(name: "HotLeadDetectedAt", table: "Leads");
            migrationBuilder.DropColumn(name: "HotLeadReason", table: "Leads");

            migrationBuilder.DropColumn(name: "SummaryJson", table: "HumanHandoffs");

            migrationBuilder.DropColumn(name: "CapturedFieldKeysJson", table: "AiInteractions");
            migrationBuilder.DropColumn(name: "AskedFieldKey", table: "AiInteractions");
            migrationBuilder.DropColumn(name: "BuyingIntentReported", table: "AiInteractions");
            migrationBuilder.DropColumn(name: "HumanRequestReported", table: "AiInteractions");
            migrationBuilder.DropColumn(name: "OptOutReported", table: "AiInteractions");
        }
    }
}
