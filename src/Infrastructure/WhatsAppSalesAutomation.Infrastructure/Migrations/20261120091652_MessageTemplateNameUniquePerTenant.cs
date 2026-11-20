using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MessageTemplateNameUniquePerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageTemplates_WhatsAppTemplateName_Language",
                table: "MessageTemplates");

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId_WhatsAppTemplateName_Language",
                table: "MessageTemplates",
                columns: new[] { "TenantId", "WhatsAppTemplateName", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageTemplates_TenantId_WhatsAppTemplateName_Language",
                table: "MessageTemplates");

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_WhatsAppTemplateName_Language",
                table: "MessageTemplates",
                columns: new[] { "WhatsAppTemplateName", "Language" },
                unique: true);
        }
    }
}
