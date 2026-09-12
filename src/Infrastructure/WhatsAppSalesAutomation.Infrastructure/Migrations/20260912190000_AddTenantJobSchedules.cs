using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantJobSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No data backfill here, unlike AddTenantTimezone's one-time UPDATE: every existing tenant's
            // four schedule rows are created by ITenantJobProvisioner.ReconcileAllAsync, which Program.cs
            // runs immediately after MigrateAsync on this very boot. Doing it there rather than in SQL
            // keeps one definition of what the default schedules are (TenantJobCatalog) instead of a
            // second copy frozen into a migration, and it is the same pass that then registers those
            // jobs with Hangfire - which a migration could not do at all.
            migrationBuilder.CreateTable(
                name: "TenantJobSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunOutcome = table.Column<int>(type: "int", nullable: true),
                    LastRunSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastRunDurationMs = table.Column<int>(type: "int", nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantJobSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantJobSchedules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantJobSchedules_TenantId_JobType",
                table: "TenantJobSchedules",
                columns: new[] { "TenantId", "JobType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantJobSchedules");
        }
    }
}
