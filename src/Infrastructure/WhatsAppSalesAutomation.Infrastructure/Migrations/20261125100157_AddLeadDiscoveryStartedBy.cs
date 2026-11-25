using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDiscoveryStartedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSkippedAtUtc",
                table: "LeadDiscoveryExecutions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SkipCount",
                table: "LeadDiscoveryExecutions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StartedBy",
                table: "LeadDiscoveryExecutions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSkippedAtUtc",
                table: "LeadDiscoveryExecutions");

            migrationBuilder.DropColumn(
                name: "SkipCount",
                table: "LeadDiscoveryExecutions");

            migrationBuilder.DropColumn(
                name: "StartedBy",
                table: "LeadDiscoveryExecutions");
        }
    }
}
