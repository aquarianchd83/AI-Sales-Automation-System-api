using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRazorpayTestPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RazorpayOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Receipt = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    PaymentId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Method = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SignatureVerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefundedMinor = table.Column<long>(type: "bigint", nullable: false),
                    LastRefundId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastWebhookEvent = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LastWebhookAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RazorpayOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RazorpayWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Event = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    PaymentId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Applied = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RazorpayWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_RazorpayOrders_CreatedAt", table: "RazorpayOrders", column: "CreatedAt");
            migrationBuilder.CreateIndex(name: "IX_RazorpayOrders_OrderId", table: "RazorpayOrders", column: "OrderId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_RazorpayOrders_PaymentId", table: "RazorpayOrders", column: "PaymentId");
            migrationBuilder.CreateIndex(name: "IX_RazorpayWebhookEvents_CreatedAt", table: "RazorpayWebhookEvents", column: "CreatedAt");
            migrationBuilder.CreateIndex(name: "IX_RazorpayWebhookEvents_EventId", table: "RazorpayWebhookEvents", column: "EventId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RazorpayWebhookEvents");
            migrationBuilder.DropTable(name: "RazorpayOrders");
        }
    }
}
