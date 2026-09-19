using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPrepaidQuotaLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PlanId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "CreditPackId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Payments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Subscription");

            migrationBuilder.AddColumn<Guid>(
                name: "RefundOfPaymentId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CreditPacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Units = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PriceCents = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditPacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlanQuotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IncludedUnits = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanQuotas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuotaGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UnitsGranted = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitsRemaining = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiredProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotaGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuotaLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntryType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UnitsDelta = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperationKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReferenceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotaLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuotaWallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotaWallets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanQuotas_PlanId_QuotaType",
                table: "PlanQuotas",
                columns: new[] { "PlanId", "QuotaType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotaGrants_TenantId_QuotaType_ExpiresAtUtc",
                table: "QuotaGrants",
                columns: new[] { "TenantId", "QuotaType", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QuotaLedgerEntries_TenantId_OperationKey",
                table: "QuotaLedgerEntries",
                columns: new[] { "TenantId", "OperationKey" });

            migrationBuilder.CreateIndex(
                name: "IX_QuotaLedgerEntries_TenantId_QuotaType_OccurredAtUtc",
                table: "QuotaLedgerEntries",
                columns: new[] { "TenantId", "QuotaType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QuotaWallets_TenantId_QuotaType",
                table: "QuotaWallets",
                columns: new[] { "TenantId", "QuotaType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditPacks");

            migrationBuilder.DropTable(
                name: "PlanQuotas");

            migrationBuilder.DropTable(
                name: "QuotaGrants");

            migrationBuilder.DropTable(
                name: "QuotaLedgerEntries");

            migrationBuilder.DropTable(
                name: "QuotaWallets");

            migrationBuilder.DropColumn(
                name: "CreditPackId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundOfPaymentId",
                table: "Payments");

            migrationBuilder.AlterColumn<Guid>(
                name: "PlanId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
