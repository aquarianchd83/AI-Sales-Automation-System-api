using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDiscoveryExecutionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeadDiscoveryExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadDiscoveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProcessingDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RootExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RetryOfExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededByExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    LastRetryAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedStep = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ErrorDetails = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    NextRetryInfo = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LockKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LockStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LockTokenReference = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LockOwnerInstanceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LockAcquiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockLastRenewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CustomersDiscovered = table.Column<int>(type: "int", nullable: false),
                    CustomersCreated = table.Column<int>(type: "int", nullable: false),
                    CustomersDuplicate = table.Column<int>(type: "int", nullable: false),
                    CustomersInvalid = table.Column<int>(type: "int", nullable: false),
                    CustomersFailed = table.Column<int>(type: "int", nullable: false),
                    CustomersSkipped = table.Column<int>(type: "int", nullable: false),
                    AutoCampaignConfigured = table.Column<bool>(type: "bit", nullable: false),
                    AutoCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferredCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferredCampaignName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GeneratedCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GeneratedCampaignName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CampaignStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CampaignNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TemplateStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MappingStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MappingsEligible = table.Column<int>(type: "int", nullable: false),
                    MappingsCreated = table.Column<int>(type: "int", nullable: false),
                    MappingsExisting = table.Column<int>(type: "int", nullable: false),
                    MappingsFailed = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeadDiscoveryGeneratedCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadDiscoveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AutoCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessingDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryGeneratedCampaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeadDiscoveryLocks",
                columns: table => new
                {
                    LockKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadDiscoveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LockToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerInstanceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AcquiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRenewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryLocks", x => x.LockKey);
                });

            migrationBuilder.CreateTable(
                name: "LeadDiscoveryExecutionCustomers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RetriedFromId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DiscoveredLeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsInvalid = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ErrorDetails = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CandidateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryExecutionCustomers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadDiscoveryExecutionCustomers_LeadDiscoveryExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "LeadDiscoveryExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeadDiscoveryExecutionTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceStepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    DelayDaysAfterPrevious = table.Column<int>(type: "int", nullable: false),
                    CampaignStepId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryExecutionTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadDiscoveryExecutionTemplates_LeadDiscoveryExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "LeadDiscoveryExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeadDiscoveryLockTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadDiscoveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessingDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TransitionAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockTokenReference = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    OwnerInstanceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryLockTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadDiscoveryLockTransitions_LeadDiscoveryExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "LeadDiscoveryExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryExecutionCustomers_ExecutionId_Status",
                table: "LeadDiscoveryExecutionCustomers",
                columns: new[] { "ExecutionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryExecutions_RootExecutionId",
                table: "LeadDiscoveryExecutions",
                column: "RootExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryExecutions_TenantId_LeadDiscoveryProfileId_Status",
                table: "LeadDiscoveryExecutions",
                columns: new[] { "TenantId", "LeadDiscoveryProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryExecutions_TenantId_ProcessingDate",
                table: "LeadDiscoveryExecutions",
                columns: new[] { "TenantId", "ProcessingDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryExecutionTemplates_ExecutionId_Sequence",
                table: "LeadDiscoveryExecutionTemplates",
                columns: new[] { "ExecutionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryGeneratedCampaigns_CampaignId",
                table: "LeadDiscoveryGeneratedCampaigns",
                column: "CampaignId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryGeneratedCampaigns_TenantId_LeadDiscoveryProfileId_AutoCampaignId_ProcessingDate",
                table: "LeadDiscoveryGeneratedCampaigns",
                columns: new[] { "TenantId", "LeadDiscoveryProfileId", "AutoCampaignId", "ProcessingDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryLockTransitions_ExecutionId_TransitionAtUtc",
                table: "LeadDiscoveryLockTransitions",
                columns: new[] { "ExecutionId", "TransitionAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadDiscoveryExecutionCustomers");

            migrationBuilder.DropTable(
                name: "LeadDiscoveryExecutionTemplates");

            migrationBuilder.DropTable(
                name: "LeadDiscoveryGeneratedCampaigns");

            migrationBuilder.DropTable(
                name: "LeadDiscoveryLocks");

            migrationBuilder.DropTable(
                name: "LeadDiscoveryLockTransitions");

            migrationBuilder.DropTable(
                name: "LeadDiscoveryExecutions");
        }
    }
}
