using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PhoneE164 = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastMessageAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastInboundAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastMessagePreview = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    MessageType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WhatsAppMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SentByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SentByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformConversationMessages_PlatformConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "PlatformConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConversationMessages_ConversationId_SentAtUtc",
                table: "PlatformConversationMessages",
                columns: new[] { "ConversationId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConversationMessages_WhatsAppMessageId",
                table: "PlatformConversationMessages",
                column: "WhatsAppMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConversations_LastMessageAtUtc",
                table: "PlatformConversations",
                column: "LastMessageAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConversations_PhoneE164",
                table: "PlatformConversations",
                column: "PhoneE164",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConversations_TenantId",
                table: "PlatformConversations",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformConversationMessages");

            migrationBuilder.DropTable(
                name: "PlatformConversations");
        }
    }
}
