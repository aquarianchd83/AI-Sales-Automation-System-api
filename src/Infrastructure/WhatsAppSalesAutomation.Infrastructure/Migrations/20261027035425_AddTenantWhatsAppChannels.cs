using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantWhatsAppChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantWhatsAppChannels",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InstanceTokenCipher = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    WebhookTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WebhookSecretHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConnectedPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StatusUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWhatsAppChannels", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWhatsAppChannels_InstanceName",
                table: "TenantWhatsAppChannels",
                column: "InstanceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantWhatsAppChannels_WebhookTokenHash",
                table: "TenantWhatsAppChannels",
                column: "WebhookTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantWhatsAppChannels");
        }
    }
}
