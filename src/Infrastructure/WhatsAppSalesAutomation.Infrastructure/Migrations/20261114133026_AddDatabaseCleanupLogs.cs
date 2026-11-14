using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseCleanupLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseCleanupLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RowsDeleted = table.Column<int>(type: "int", nullable: false),
                    KeepDays = table.Column<int>(type: "int", nullable: false),
                    PercentUsedBefore = table.Column<double>(type: "float", nullable: false),
                    UsedMbBefore = table.Column<double>(type: "float", nullable: false),
                    UsedMbAfter = table.Column<double>(type: "float", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseCleanupLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseCleanupLogs_CreatedAt",
                table: "DatabaseCleanupLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseCleanupLogs_RunId",
                table: "DatabaseCleanupLogs",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseCleanupLogs");
        }
    }
}
