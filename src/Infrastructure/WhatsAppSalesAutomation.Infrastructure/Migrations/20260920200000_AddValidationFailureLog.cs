using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddValidationFailureLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every output check that did not pass, one row each. Rows rather than a list column on
            // the turn: the useful question is which check fires most and whether it is rising after a
            // prompt change, and a comma-separated column answers neither without reading the whole
            // window back out and splitting it.
            migrationBuilder.CreateTable(
                name: "AiInteractionValidationFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiInteractionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Blocking = table.Column<bool>(type: "bit", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInteractionValidationFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiInteractionValidationFailures_AiInteractions_AiInteractionId",
                        column: x => x.AiInteractionId,
                        principalTable: "AiInteractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractionValidationFailures_AiInteractionId",
                table: "AiInteractionValidationFailures",
                column: "AiInteractionId");

            // The report groups by code inside a date window; CreatedAt is the window column.
            migrationBuilder.CreateIndex(
                name: "IX_AiInteractionValidationFailures_Code_CreatedAt",
                table: "AiInteractionValidationFailures",
                columns: new[] { "Code", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AiInteractionValidationFailures");
        }
    }
}
