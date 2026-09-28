using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkLeadDiscoveryRunToExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionId",
                table: "LeadDiscoveryRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryRuns_ExecutionId",
                table: "LeadDiscoveryRuns",
                column: "ExecutionId");

            // Backfill runs recorded since executions were introduced: a run is written at the end of its
            // execution's research, so it falls inside that execution's window. Executions of one profile never
            // overlap (the tenant+profile lock), and only a non-retry execution researches. Older runs predate
            // executions and stay null.
            migrationBuilder.Sql(@"
UPDATE r
SET r.ExecutionId = e.Id
FROM LeadDiscoveryRuns r
CROSS APPLY (
    SELECT TOP 1 x.Id
    FROM LeadDiscoveryExecutions x
    WHERE x.TenantId = r.TenantId
      AND x.RetryOfExecutionId IS NULL
      AND x.StartedAtUtc <= r.RanAtUtc
      AND (x.EndedAtUtc IS NULL OR x.EndedAtUtc >= r.RanAtUtc)
    ORDER BY x.StartedAtUtc DESC
) e
WHERE r.ExecutionId IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeadDiscoveryRuns_ExecutionId",
                table: "LeadDiscoveryRuns");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "LeadDiscoveryRuns");
        }
    }
}
