using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentStudioFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudioActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Units = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitsRefunded = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ReferenceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudioActivities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantStudioSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalPolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantStudioSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudioActivities_TenantId_OperationKey",
                table: "StudioActivities",
                columns: new[] { "TenantId", "OperationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudioActivities_TenantId_RequestedAtUtc",
                table: "StudioActivities",
                columns: new[] { "TenantId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantStudioSettings_TenantId",
                table: "TenantStudioSettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudioActivities");

            migrationBuilder.DropTable(
                name: "TenantStudioSettings");
        }
    }
}
