using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantWhatsAppTokenLifetime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No backfill, deliberately. Every existing tenant starts with both columns null, which
            // TenantWhatsAppTokenRefreshService reads as "never checked" - so each tenant's first
            // whatsapp-token-refresh run exchanges its token once and records Meta's real answer
            // (an expiry date, or none for a permanent System User token). Seeding any value here would
            // be a guess about a token this migration cannot see.
            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenExpiresAtUtc",
                table: "TenantWhatsAppConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenRefreshedAtUtc",
                table: "TenantWhatsAppConfigs",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessTokenExpiresAtUtc",
                table: "TenantWhatsAppConfigs");

            migrationBuilder.DropColumn(
                name: "AccessTokenRefreshedAtUtc",
                table: "TenantWhatsAppConfigs");
        }
    }
}
