using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRazorpayAutoPay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelAtPeriodEnd",
                table: "Subscriptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RazorpaySubscriptionRowId",
                table: "Subscriptions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "RazorpayWebhookEvents",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EventCreatedAtUtc",
                table: "RazorpayWebhookEvents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessedAtUtc",
                table: "RazorpayWebhookEvents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessingStatus",
                table: "RazorpayWebhookEvents",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Every event stored before this was applied in the request that received it, so none is waiting.
                defaultValue: "Completed");

            migrationBuilder.Sql("UPDATE [RazorpayWebhookEvents] SET [ProcessingStatus] = 'Ignored' WHERE [Applied] = 0;");
            migrationBuilder.Sql("UPDATE [RazorpayWebhookEvents] SET [ProcessedAtUtc] = [CreatedAt];");

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "RazorpayWebhookEvents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionId",
                table: "RazorpayWebhookEvents",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "Payments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Payments",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderInvoiceId",
                table: "Payments",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentId",
                table: "Payments",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubscriptionId",
                table: "Payments",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Payments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Every payment recorded so far was simulated and could not fail.
                defaultValue: "Succeeded");

            migrationBuilder.CreateTable(
                name: "RazorpayPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    RazorpayPlanId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RazorpayPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RazorpayPlans_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RazorpaySubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RazorpayPlanRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RazorpaySubscriptionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ShortUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TotalCount = table.Column<int>(type: "int", nullable: false),
                    PaidCount = table.Column<int>(type: "int", nullable: false),
                    CurrentStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChargeAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AuthenticatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelAtCycleEnd = table.Column<bool>(type: "bit", nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CustomerId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LastEventAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastEvent = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LastReconciledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RazorpaySubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RazorpaySubscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RazorpaySubscriptions_RazorpayPlans_RazorpayPlanRowId",
                        column: x => x.RazorpayPlanRowId,
                        principalTable: "RazorpayPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RazorpaySubscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_RazorpaySubscriptionRowId",
                table: "Subscriptions",
                column: "RazorpaySubscriptionRowId");

            migrationBuilder.CreateIndex(
                name: "IX_RazorpayWebhookEvents_ProcessingStatus_CreatedAt",
                table: "RazorpayWebhookEvents",
                columns: new[] { "ProcessingStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RazorpayWebhookEvents_SubscriptionId",
                table: "RazorpayWebhookEvents",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Provider_ProviderPaymentId",
                table: "Payments",
                columns: new[] { "Provider", "ProviderPaymentId" },
                unique: true,
                filter: "[ProviderPaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderSubscriptionId",
                table: "Payments",
                column: "ProviderSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_RazorpayPlans_PlanId_Mode_Currency_AmountMinor_Period_Interval",
                table: "RazorpayPlans",
                columns: new[] { "PlanId", "Mode", "Currency", "AmountMinor", "Period", "Interval" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RazorpayPlans_RazorpayPlanId",
                table: "RazorpayPlans",
                column: "RazorpayPlanId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RazorpaySubscriptions_PlanId",
                table: "RazorpaySubscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_RazorpaySubscriptions_RazorpayPlanRowId",
                table: "RazorpaySubscriptions",
                column: "RazorpayPlanRowId");

            migrationBuilder.CreateIndex(
                name: "IX_RazorpaySubscriptions_RazorpaySubscriptionId",
                table: "RazorpaySubscriptions",
                column: "RazorpaySubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RazorpaySubscriptions_Status_LastReconciledAtUtc",
                table: "RazorpaySubscriptions",
                columns: new[] { "Status", "LastReconciledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RazorpaySubscriptions_TenantId_PendingAuthorization",
                table: "RazorpaySubscriptions",
                column: "TenantId",
                unique: true,
                filter: "[Status] = 'Created'");

            migrationBuilder.CreateIndex(
                name: "IX_RazorpaySubscriptions_TenantId_Status",
                table: "RazorpaySubscriptions",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_RazorpaySubscriptions_RazorpaySubscriptionRowId",
                table: "Subscriptions",
                column: "RazorpaySubscriptionRowId",
                principalTable: "RazorpaySubscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_RazorpaySubscriptions_RazorpaySubscriptionRowId",
                table: "Subscriptions");

            migrationBuilder.DropTable(
                name: "RazorpaySubscriptions");

            migrationBuilder.DropTable(
                name: "RazorpayPlans");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_RazorpaySubscriptionRowId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_RazorpayWebhookEvents_ProcessingStatus_CreatedAt",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_RazorpayWebhookEvents_SubscriptionId",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Provider_ProviderPaymentId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ProviderSubscriptionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CancelAtPeriodEnd",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RazorpaySubscriptionRowId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropColumn(
                name: "EventCreatedAtUtc",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropColumn(
                name: "ProcessedAtUtc",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropColumn(
                name: "ProcessingStatus",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                table: "RazorpayWebhookEvents");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderInvoiceId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderPaymentId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderSubscriptionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Payments");
        }
    }
}
