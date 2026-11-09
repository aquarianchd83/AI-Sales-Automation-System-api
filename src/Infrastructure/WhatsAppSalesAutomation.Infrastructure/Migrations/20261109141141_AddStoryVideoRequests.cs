using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryVideoRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoryVideoRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    RaisedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaisedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScriptGeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VideoSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    VideoStorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VideoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    VideoFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    VideoReadyAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VideoAttachedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryVideoRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoryVideoRequests_ProjectId",
                table: "StoryVideoRequests",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoryVideoRequests_Status_RaisedAtUtc",
                table: "StoryVideoRequests",
                columns: new[] { "Status", "RaisedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryVideoRequests_TenantId_RaisedAtUtc",
                table: "StoryVideoRequests",
                columns: new[] { "TenantId", "RaisedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoryVideoRequests");
        }
    }
}
