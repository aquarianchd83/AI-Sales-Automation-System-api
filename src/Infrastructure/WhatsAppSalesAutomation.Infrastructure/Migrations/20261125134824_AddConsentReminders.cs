using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConsentReminderEnabled",
                table: "LeadDiscoveryProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ConsentReminderFirstDays",
                table: "LeadDiscoveryProfiles",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "ConsentReminderSecondDays",
                table: "LeadDiscoveryProfiles",
                type: "int",
                nullable: true,
                defaultValue: 7);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReminderAtUtc",
                table: "ConsentInvitations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderCount",
                table: "ConsentInvitations",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsentReminderEnabled",
                table: "LeadDiscoveryProfiles");

            migrationBuilder.DropColumn(
                name: "ConsentReminderFirstDays",
                table: "LeadDiscoveryProfiles");

            migrationBuilder.DropColumn(
                name: "ConsentReminderSecondDays",
                table: "LeadDiscoveryProfiles");

            migrationBuilder.DropColumn(
                name: "LastReminderAtUtc",
                table: "ConsentInvitations");

            migrationBuilder.DropColumn(
                name: "ReminderCount",
                table: "ConsentInvitations");
        }
    }
}
