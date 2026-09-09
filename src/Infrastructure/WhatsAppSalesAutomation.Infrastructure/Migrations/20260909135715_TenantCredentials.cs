using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantAiProviderConfigs",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EmbeddingProvider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AnthropicApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AnthropicModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnthropicApiVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnthropicBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenAiApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OpenAiChatModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenAiEmbeddingModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenAiBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GoogleApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GoogleChatModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GoogleEmbeddingModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GoogleBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAiProviderConfigs", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "TenantWhatsAppConfigs",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneNumberId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WhatsAppBusinessAccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccessToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WebhookVerifyToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApiVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsConnected = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWhatsAppConfigs", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWhatsAppConfigs_PhoneNumberId",
                table: "TenantWhatsAppConfigs",
                column: "PhoneNumberId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantAiProviderConfigs");

            migrationBuilder.DropTable(
                name: "TenantWhatsAppConfigs");
        }
    }
}
