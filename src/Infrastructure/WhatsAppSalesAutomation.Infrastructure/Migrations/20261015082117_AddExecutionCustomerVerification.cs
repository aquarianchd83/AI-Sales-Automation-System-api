using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionCustomerVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerificationReason",
                table: "LeadDiscoveryExecutionCustomers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationReasonCode",
                table: "LeadDiscoveryExecutionCustomers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationRequestId",
                table: "LeadDiscoveryExecutionCustomers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationStatus",
                table: "LeadDiscoveryExecutionCustomers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAtUtc",
                table: "LeadDiscoveryExecutionCustomers",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationReason",
                table: "LeadDiscoveryExecutionCustomers");

            migrationBuilder.DropColumn(
                name: "VerificationReasonCode",
                table: "LeadDiscoveryExecutionCustomers");

            migrationBuilder.DropColumn(
                name: "VerificationRequestId",
                table: "LeadDiscoveryExecutionCustomers");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                table: "LeadDiscoveryExecutionCustomers");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUtc",
                table: "LeadDiscoveryExecutionCustomers");
        }
    }
}
