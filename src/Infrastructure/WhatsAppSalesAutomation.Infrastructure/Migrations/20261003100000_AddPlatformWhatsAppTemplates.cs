using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformWhatsAppTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformMediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageProvider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Checksum = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMediaAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformMessageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WhatsAppTemplateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WhatsAppTemplateStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BodyText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MetaTemplateId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastPushedBodyText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    HeaderMediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HeaderOnMeta = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMessageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformMessageTemplates_PlatformMediaAssets_HeaderMediaAssetId",
                        column: x => x.HeaderMediaAssetId,
                        principalTable: "PlatformMediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMediaAssets_Checksum",
                table: "PlatformMediaAssets",
                column: "Checksum");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMessageTemplates_EventKey",
                table: "PlatformMessageTemplates",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMessageTemplates_HeaderMediaAssetId",
                table: "PlatformMessageTemplates",
                column: "HeaderMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMessageTemplates_WhatsAppTemplateName_Language",
                table: "PlatformMessageTemplates",
                columns: new[] { "WhatsAppTemplateName", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformMessageTemplates");

            migrationBuilder.DropTable(
                name: "PlatformMediaAssets");
        }
    }
}
