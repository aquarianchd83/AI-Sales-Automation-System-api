using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261102180000_ReplaceBookingInviteWithConfirmation")]
    public partial class ReplaceBookingInviteWithConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No calendar invitation is sent any more - a plain confirmation email goes instead - so the start and length it was built from are gone.
            migrationBuilder.DropColumn(name: "StartsAtUtc", table: "NextStepBookings");
            migrationBuilder.DropColumn(name: "DurationMinutes", table: "NextStepBookings");
            migrationBuilder.RenameColumn(name: "InviteStatus", table: "NextStepBookings", newName: "ConfirmationStatus");
            migrationBuilder.RenameColumn(name: "InviteNote", table: "NextStepBookings", newName: "ConfirmationNote");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "ConfirmationStatus", table: "NextStepBookings", newName: "InviteStatus");
            migrationBuilder.RenameColumn(name: "ConfirmationNote", table: "NextStepBookings", newName: "InviteNote");
            migrationBuilder.AddColumn<DateTime>(name: "StartsAtUtc", table: "NextStepBookings", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<int>(name: "DurationMinutes", table: "NextStepBookings", type: "int", nullable: true);
        }
    }
}
