using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServingIndexLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiInteractionSources_KnowledgeBaseChunks_KnowledgeBaseChunkId",
                table: "AiInteractionSources");

            migrationBuilder.AddColumn<string>(
                name: "ServingError",
                table: "KnowledgeVersions",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ServingIndexedAt",
                table: "KnowledgeVersions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServingStatus",
                table: "KnowledgeVersions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Failed");

            // Versions published before chat serving existed have no serving index: they are shown as needing one ("Try again" builds it).
            migrationBuilder.Sql("UPDATE KnowledgeVersions SET ServingError = N'Published before chat serving existed. Use Try again to build its index.' WHERE ServingStatus = N'Failed' AND ServingError IS NULL");

            migrationBuilder.AddColumn<Guid>(
                name: "KnowledgeItemId",
                table: "KnowledgeBaseChunks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "KnowledgeVersionId",
                table: "KnowledgeBaseChunks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "KnowledgeBaseArticles",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "KnowledgeBaseChunkId",
                table: "AiInteractionSources",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "KnowledgeItemId",
                table: "AiInteractionSources",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "KnowledgeVersionId",
                table: "AiInteractionSources",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseChunks_KnowledgeItemId",
                table: "KnowledgeBaseChunks",
                column: "KnowledgeItemId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseArticles_TenantId_SystemKey",
                table: "KnowledgeBaseArticles",
                columns: new[] { "TenantId", "SystemKey" },
                unique: true,
                filter: "[SystemKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractionSources_KnowledgeItemId",
                table: "AiInteractionSources",
                column: "KnowledgeItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_AiInteractionSources_KnowledgeBaseChunks_KnowledgeBaseChunkId",
                table: "AiInteractionSources",
                column: "KnowledgeBaseChunkId",
                principalTable: "KnowledgeBaseChunks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiInteractionSources_KnowledgeBaseChunks_KnowledgeBaseChunkId",
                table: "AiInteractionSources");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeBaseChunks_KnowledgeItemId",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeBaseArticles_TenantId_SystemKey",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropIndex(
                name: "IX_AiInteractionSources_KnowledgeItemId",
                table: "AiInteractionSources");

            migrationBuilder.DropColumn(
                name: "ServingError",
                table: "KnowledgeVersions");

            migrationBuilder.DropColumn(
                name: "ServingIndexedAt",
                table: "KnowledgeVersions");

            migrationBuilder.DropColumn(
                name: "ServingStatus",
                table: "KnowledgeVersions");

            migrationBuilder.DropColumn(
                name: "KnowledgeItemId",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "KnowledgeVersionId",
                table: "KnowledgeBaseChunks");

            migrationBuilder.DropColumn(
                name: "SystemKey",
                table: "KnowledgeBaseArticles");

            migrationBuilder.DropColumn(
                name: "KnowledgeItemId",
                table: "AiInteractionSources");

            migrationBuilder.DropColumn(
                name: "KnowledgeVersionId",
                table: "AiInteractionSources");

            migrationBuilder.AlterColumn<Guid>(
                name: "KnowledgeBaseChunkId",
                table: "AiInteractionSources",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AiInteractionSources_KnowledgeBaseChunks_KnowledgeBaseChunkId",
                table: "AiInteractionSources",
                column: "KnowledgeBaseChunkId",
                principalTable: "KnowledgeBaseChunks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
