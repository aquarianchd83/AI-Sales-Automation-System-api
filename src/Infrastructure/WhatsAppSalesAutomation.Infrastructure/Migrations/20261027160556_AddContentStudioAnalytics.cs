using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentStudioAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MetricsRefreshedAtUtc",
                table: "Publications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MissingOnPlatformSinceUtc",
                table: "Publications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PublicationMetricSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Views = table.Column<long>(type: "bigint", nullable: false),
                    Likes = table.Column<long>(type: "bigint", nullable: false),
                    Comments = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationMetricSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationMetricSnapshots_TenantId_PublicationId_CapturedAtUtc",
                table: "PublicationMetricSnapshots",
                columns: new[] { "TenantId", "PublicationId", "CapturedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationMetricSnapshots");

            migrationBuilder.DropColumn(
                name: "MetricsRefreshedAtUtc",
                table: "Publications");

            migrationBuilder.DropColumn(
                name: "MissingOnPlatformSinceUtc",
                table: "Publications");
        }
    }
}
