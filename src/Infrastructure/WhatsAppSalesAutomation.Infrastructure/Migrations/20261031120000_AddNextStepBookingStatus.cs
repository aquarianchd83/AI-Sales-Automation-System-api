using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261031120000_AddNextStepBookingStatus")]
    public partial class AddNextStepBookingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every booking made so far is still waiting to happen as far as the system knows, so each starts Pending.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "NextStepBookings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "NextStepBookings");
        }
    }
}
