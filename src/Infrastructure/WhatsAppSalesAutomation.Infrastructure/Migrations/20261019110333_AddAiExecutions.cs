using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Task = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    RequestedProvider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    FallbackFromProvider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ErrorSummary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_CorrelationId",
                table: "AiExecutions",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_CreatedAt",
                table: "AiExecutions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_Provider_Task_Status_CreatedAt",
                table: "AiExecutions",
                columns: new[] { "Provider", "Task", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiExecutions");
        }
    }
}
