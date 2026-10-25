using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppConsentPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DecisionAtUtc",
                table: "ConsentInvitations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionConsentText",
                table: "ConsentInvitations",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionIpHash",
                table: "ConsentInvitations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecisionPageVersion",
                table: "ConsentInvitations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionSource",
                table: "ConsentInvitations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionStatus",
                table: "ConsentInvitations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "DecisionUserAgent",
                table: "ConsentInvitations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HeldForConsentPage",
                table: "ConsentInvitations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ConsentInvitations",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<DateTime>(
                name: "TokenExpiresAtUtc",
                table: "ConsentInvitations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "ConsentInvitations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhatsAppAttemptCount",
                table: "ConsentInvitations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "WhatsAppInitiatedAtUtc",
                table: "ConsentInvitations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppInitiation",
                table: "ConsentInvitations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppInitiationError",
                table: "ConsentInvitations",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConsentPageVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ProductLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProductDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AdditionalInfo = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ConsentStatement = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PrivacyPolicyUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TermsUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentPageVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantConsentPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ProductDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AdditionalInfo = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ConsentStatement = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PrivacyPolicyUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TermsUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PublishedVersion = table.Column<int>(type: "int", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HasUnpublishedChanges = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantConsentPages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentInvitations_TenantId_DecisionStatus",
                table: "ConsentInvitations",
                columns: new[] { "TenantId", "DecisionStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentInvitations_TokenHash",
                table: "ConsentInvitations",
                column: "TokenHash",
                unique: true,
                filter: "[TokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentPageVersions_TenantId_Version",
                table: "ConsentPageVersions",
                columns: new[] { "TenantId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantConsentPages_TenantId",
                table: "TenantConsentPages",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsentPageVersions");

            migrationBuilder.DropTable(
                name: "TenantConsentPages");

            migrationBuilder.DropIndex(
                name: "IX_ConsentInvitations_TenantId_DecisionStatus",
                table: "ConsentInvitations");

            migrationBuilder.DropIndex(
                name: "IX_ConsentInvitations_TokenHash",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionAtUtc",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionConsentText",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionIpHash",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionPageVersion",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionSource",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionStatus",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "DecisionUserAgent",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "HeldForConsentPage",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "TokenExpiresAtUtc",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "WhatsAppAttemptCount",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "WhatsAppInitiatedAtUtc",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "WhatsAppInitiation",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "WhatsAppInitiationError",
                table: "ConsentInvitations");
        }
    }
}
