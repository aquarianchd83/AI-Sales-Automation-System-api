using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentStudioReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedHash",
                table: "ContentVersions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewComment",
                table: "ContentVersions",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                table: "ContentVersions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedBy",
                table: "ContentVersions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevisedFromNumber",
                table: "ContentVersions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedAt",
                table: "ContentVersions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmittedBy",
                table: "ContentVersions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContentReviewEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentReviewEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentReviewEvents_TenantId_VersionId_At",
                table: "ContentReviewEvents",
                columns: new[] { "TenantId", "VersionId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentReviewEvents");

            migrationBuilder.DropColumn(
                name: "ApprovedHash",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "ReviewComment",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "RevisedFromNumber",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                table: "ContentVersions");

            migrationBuilder.DropColumn(
                name: "SubmittedBy",
                table: "ContentVersions");
        }
    }
}
