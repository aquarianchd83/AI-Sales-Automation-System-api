using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261102140000_AddNextStepBookingInvite")]
    public partial class AddNextStepBookingInvite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerEmail",
                table: "NextStepBookings",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartsAtUtc",
                table: "NextStepBookings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMinutes",
                table: "NextStepBookings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InviteStatus",
                table: "NextStepBookings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InviteNote",
                table: "NextStepBookings",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerEmail",
                table: "NextStepBookings");

            migrationBuilder.DropColumn(
                name: "StartsAtUtc",
                table: "NextStepBookings");

            migrationBuilder.DropColumn(
                name: "DurationMinutes",
                table: "NextStepBookings");

            migrationBuilder.DropColumn(
                name: "InviteStatus",
                table: "NextStepBookings");

            migrationBuilder.DropColumn(
                name: "InviteNote",
                table: "NextStepBookings");
        }
    }
}
