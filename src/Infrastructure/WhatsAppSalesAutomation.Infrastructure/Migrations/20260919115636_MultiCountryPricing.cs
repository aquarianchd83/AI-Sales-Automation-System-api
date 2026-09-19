using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultiCountryPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StateCode",
                table: "Tenants",
                type: "nvarchar(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountInr",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "Payments",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxRateToInr",
                table: "Payments",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodEndUtc",
                table: "Payments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodStartUtc",
                table: "Payments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateCode",
                table: "Payments",
                type: "nvarchar(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxLinesJson",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxLocal",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalLocal",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Multi-country pricing has no converted fallback, so nothing that was on sale may quietly stop being sold: every
            // plan and credit pack gets an explicit price in every country the platform prices for, worked out ONCE here from
            // its old USD price. From now on these are ordinary prices the operator edits; nothing converts again.
            foreach (var region in WhatsAppSalesAutomation.Application.Billing.RegionalPricingCatalog.All
                         .GroupBy(r => r.CountryCode).Select(g => g.First()))
            {
                var rate = region.RateToUsd.ToString(System.Globalization.CultureInfo.InvariantCulture);
                migrationBuilder.Sql($"INSERT INTO PlanPrices (Id, PlanId, CountryCode, Amount, CreatedAt) SELECT NEWID(), p.Id, '{region.CountryCode}', ROUND(p.PriceMonthlyCents / 100.0 * {rate}, 2), SYSUTCDATETIME() FROM Plans p WHERE p.PriceMonthlyCents > 0 AND NOT EXISTS (SELECT 1 FROM PlanPrices x WHERE x.PlanId = p.Id AND x.CountryCode = '{region.CountryCode}')");
                migrationBuilder.Sql($"INSERT INTO CreditPackPrices (Id, CreditPackId, CountryCode, Amount, CreatedAt) SELECT NEWID(), c.Id, '{region.CountryCode}', ROUND(c.PriceCents / 100.0 * {rate}, 2), SYSUTCDATETIME() FROM CreditPacks c WHERE c.PriceCents > 0 AND NOT EXISTS (SELECT 1 FROM CreditPackPrices x WHERE x.CreditPackId = c.Id AND x.CountryCode = '{region.CountryCode}')");

            }

            // Payments made before this get their rupee equivalent, at the platform's rate when this ran (83 rupees a dollar).
            // Their country and tax stay empty: they predate both.
            foreach (var region in WhatsAppSalesAutomation.Application.Billing.RegionalPricingCatalog.All.GroupBy(r => r.CurrencyCode).Select(g => g.First()))
            {
                var rate = region.RateToUsd.ToString(System.Globalization.CultureInfo.InvariantCulture);
                migrationBuilder.Sql($"UPDATE Payments SET FxRateToInr = ROUND(83.0 / {rate}, 6), AmountInr = ROUND(LocalAmount / {rate} * 83.0, 2) WHERE AmountInr = 0 AND CurrencyCode = '{region.CurrencyCode}'");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StateCode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AmountInr",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "FxRateToInr",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PeriodEndUtc",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PeriodStartUtc",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "StateCode",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TaxLinesJson",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TaxLocal",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TotalLocal",
                table: "Payments");
        }
    }
}
