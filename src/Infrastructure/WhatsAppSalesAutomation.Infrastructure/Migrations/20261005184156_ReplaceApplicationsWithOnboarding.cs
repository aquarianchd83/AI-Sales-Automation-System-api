using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceApplicationsWithOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationExecutions");

            migrationBuilder.DropTable(
                name: "ApplicationSetupAuditEntries");

            migrationBuilder.DropTable(
                name: "ApplicationSetupValues");

            migrationBuilder.DropTable(
                name: "PlanRequirements");

            migrationBuilder.DropTable(
                name: "PlanApplications");

            migrationBuilder.DropTable(
                name: "PlanSetupVersions");

            migrationBuilder.AddColumn<string>(
                name: "VerificationError",
                table: "TenantWhatsAppConfigs",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAtUtc",
                table: "TenantWhatsAppConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedDisplayPhoneNumber",
                table: "TenantWhatsAppConfigs",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedName",
                table: "TenantWhatsAppConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OnboardingCompletedAt",
                table: "Tenants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlatformPlanId",
                table: "SalesPackages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantOnboardingSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOnboardingSteps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOnboardingSteps_TenantId_StepKey",
                table: "TenantOnboardingSteps",
                columns: new[] { "TenantId", "StepKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantOnboardingSteps");

            migrationBuilder.DropColumn(
                name: "VerificationError",
                table: "TenantWhatsAppConfigs");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUtc",
                table: "TenantWhatsAppConfigs");

            migrationBuilder.DropColumn(
                name: "VerifiedDisplayPhoneNumber",
                table: "TenantWhatsAppConfigs");

            migrationBuilder.DropColumn(
                name: "VerifiedName",
                table: "TenantWhatsAppConfigs");

            migrationBuilder.DropColumn(
                name: "OnboardingCompletedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PlatformPlanId",
                table: "SalesPackages");

            migrationBuilder.CreateTable(
                name: "PlanSetupVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReleaseNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValidityDays = table.Column<int>(type: "int", nullable: true),
                    VersionNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanSetupVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanSetupVersions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutionCount = table.Column<int>(type: "int", nullable: false),
                    LastExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanSetupVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SetupCompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SetupCompletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SetupExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SetupStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanApplications_PlanSetupVersions_PlanSetupVersionId",
                        column: x => x.PlanSetupVersionId,
                        principalTable: "PlanSetupVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanApplications_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConditionFieldKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ConditionOperator = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ConditionValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DefaultValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    FieldType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HelpText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    MetricKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    OptionsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PlanSetupVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValidationJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanRequirements_PlanSetupVersions_PlanSetupVersionId",
                        column: x => x.PlanSetupVersionId,
                        principalTable: "PlanSetupVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanSetupVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanVersionLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SetupSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationExecutions_PlanApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "PlanApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSetupAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ImpersonatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PerformedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PerformedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlanSetupVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanVersionLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSetupAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationSetupAuditEntries_PlanApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "PlanApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSetupValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    FieldValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSetupValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationSetupValues_PlanApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "PlanApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationExecutions_ApplicationId_StartedAt",
                table: "ApplicationExecutions",
                columns: new[] { "ApplicationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSetupAuditEntries_ApplicationId_PerformedAt",
                table: "ApplicationSetupAuditEntries",
                columns: new[] { "ApplicationId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSetupValues_ApplicationId_FieldKey",
                table: "ApplicationSetupValues",
                columns: new[] { "ApplicationId", "FieldKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanApplications_PlanId",
                table: "PlanApplications",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanApplications_PlanSetupVersionId",
                table: "PlanApplications",
                column: "PlanSetupVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanApplications_TenantId_Name",
                table: "PlanApplications",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanRequirements_PlanSetupVersionId_FieldKey",
                table: "PlanRequirements",
                columns: new[] { "PlanSetupVersionId", "FieldKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanSetupVersions_PlanId_Status",
                table: "PlanSetupVersions",
                columns: new[] { "PlanId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanSetupVersions_PlanId_VersionNumber",
                table: "PlanSetupVersions",
                columns: new[] { "PlanId", "VersionNumber" },
                unique: true);
        }
    }
}
