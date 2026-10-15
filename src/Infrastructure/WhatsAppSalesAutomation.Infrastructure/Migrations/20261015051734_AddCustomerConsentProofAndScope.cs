using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerConsentProofAndScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OptInProof",
                table: "Customers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptInScope",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            // Existing opt-ins that already carried a date and a source were messageable before this change.
            // They are kept messageable, but stamped so nobody mistakes them for verified consent: the proof
            // says "legacy", and the scope is ASSUMED to be Marketing because that is what they were used for.
            // An opt-in with no date or no source was never complete and stays blocked by the send gate.
            migrationBuilder.Sql(
                "UPDATE Customers SET OptInScope = 'Marketing', " +
                "OptInProof = 'Legacy record (recorded before proof was captured)' " +
                "WHERE OptInStatus = 'OptedIn' AND OptInTimestamp IS NOT NULL AND OptInSource IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptInProof",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "OptInScope",
                table: "Customers");
        }
    }
}
