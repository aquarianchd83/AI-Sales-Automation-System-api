using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLatencyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_AiInteractions_ConversationId",
                table: "AiInteractions");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_CreatedAt",
                table: "Messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ScoreNumeric",
                table: "Leads",
                columns: new[] { "TenantId", "ScoreNumeric" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_Stage_ScoreNumeric",
                table: "Leads",
                columns: new[] { "TenantId", "Stage", "ScoreNumeric" });

            migrationBuilder.CreateIndex(
                name: "IX_HumanHandoffs_TenantId_Status",
                table: "HumanHandoffs",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_CreatedAt",
                table: "Customers",
                columns: new[] { "TenantId", "CreatedAt" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations",
                columns: new[] { "TenantId", "Status", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId_CreatedAt",
                table: "Campaigns",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_ConversationId_ActionTaken_CreatedAt",
                table: "AiInteractions",
                columns: new[] { "ConversationId", "ActionTaken", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_CreatedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_ScoreNumeric",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_Stage_ScoreNumeric",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_HumanHandoffs_TenantId_Status",
                table: "HumanHandoffs");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_CreatedAt",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Campaigns_TenantId_CreatedAt",
                table: "Campaigns");

            migrationBuilder.DropIndex(
                name: "IX_AiInteractions_ConversationId_ActionTaken_CreatedAt",
                table: "AiInteractions");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_ConversationId",
                table: "AiInteractions",
                column: "ConversationId");
        }
    }
}
