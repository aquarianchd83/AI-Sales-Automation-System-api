using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantWhatsAppQualityState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualityRating",
                table: "Tenants",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Green");

            migrationBuilder.AddColumn<DateTime>(
                name: "QualityRatingSince",
                table: "Tenants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QualityWarningAt",
                table: "Tenants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SendingPauseReason",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendingPausedAt",
                table: "Tenants",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualityRating",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "QualityRatingSince",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "QualityWarningAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SendingPauseReason",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SendingPausedAt",
                table: "Tenants");
        }
    }
}
