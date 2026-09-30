using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageTemplateHeaderImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HeaderMediaAssetId",
                table: "MessageTemplates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HeaderOnMeta",
                table: "MessageTemplates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_HeaderMediaAssetId",
                table: "MessageTemplates",
                column: "HeaderMediaAssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_MessageTemplates_MediaAssets_HeaderMediaAssetId",
                table: "MessageTemplates",
                column: "HeaderMediaAssetId",
                principalTable: "MediaAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MessageTemplates_MediaAssets_HeaderMediaAssetId",
                table: "MessageTemplates");

            migrationBuilder.DropIndex(
                name: "IX_MessageTemplates_HeaderMediaAssetId",
                table: "MessageTemplates");

            migrationBuilder.DropColumn(
                name: "HeaderMediaAssetId",
                table: "MessageTemplates");

            migrationBuilder.DropColumn(
                name: "HeaderOnMeta",
                table: "MessageTemplates");
        }
    }
}
