using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConversationActivityAtSortKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations");

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityAt",
                table: "Conversations",
                type: "datetime2",
                nullable: false,
                computedColumnSql: "COALESCE([LastMessageAt], [CreatedAt])",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_ActivityAt",
                table: "Conversations",
                columns: new[] { "TenantId", "ActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_Status_ActivityAt",
                table: "Conversations",
                columns: new[] { "TenantId", "Status", "ActivityAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_ActivityAt",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_Status_ActivityAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ActivityAt",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations",
                columns: new[] { "TenantId", "Status", "LastMessageAt" });
        }
    }
}
