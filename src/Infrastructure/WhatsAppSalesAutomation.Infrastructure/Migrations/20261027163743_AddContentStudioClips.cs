using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentStudioClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClipCount",
                table: "RenderJobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "RenderJobs",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Style",
                table: "ContentVersions",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CameraMove",
                table: "ContentScenes",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "ContentScenes",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ShotType",
                table: "ContentScenes",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transition",
                table: "ContentScenes",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RenderClips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SceneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    ExternalRenderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OutputStorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Seconds = table.Column<double>(type: "float", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenderClips", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RenderClips_TenantId_JobId_Order",
                table: "RenderClips",
                columns: new[] { "TenantId", "JobId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RenderClips");

            migrationBuilder.DropColumn(
                name: "ClipCount",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Style",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "CameraMove",
                table: "ContentScenes");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "ContentScenes");

            migrationBuilder.DropColumn(
                name: "ShotType",
                table: "ContentScenes");

            migrationBuilder.DropColumn(
                name: "Transition",
                table: "ContentScenes");
        }
    }
}
