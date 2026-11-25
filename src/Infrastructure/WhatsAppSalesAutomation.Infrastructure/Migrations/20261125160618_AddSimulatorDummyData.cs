using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Application.Simulator;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulatorDummyData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SimulatorDummyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    BusinessType = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ContactPerson = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    City = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LeadScore = table.Column<int>(type: "int", nullable: true),
                    Variant = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulatorDummyRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SimulatorDummyRecords_Category_DisplayOrder",
                table: "SimulatorDummyRecords",
                columns: new[] { "Category", "DisplayOrder" });

            // The starter dummy data, inserted once. From here on the platform admin owns it on the Simulator dummy data page: a row they
            // delete stays deleted (unlike a startup seeder), and "Restore defaults" there adds the missing starter rows back.
            var now = DateTime.UtcNow;
            foreach (var r in SimulatorDataCatalog.DefaultRecords())
            {
                migrationBuilder.InsertData(
                    table: "SimulatorDummyRecords",
                    columns: new[] { "Id", "Category", "Name", "Value", "BusinessType", "ContactPerson", "Address", "City", "Phone", "Email", "Website", "LeadScore", "Variant", "IsActive", "DisplayOrder", "CreatedAt", "UpdatedAt" },
                    values: new object[] { r.Id, r.Category.ToString(), r.Name, r.Value, r.BusinessType, r.ContactPerson, r.Address, r.City, r.Phone, r.Email, r.Website, r.LeadScore, r.Variant?.ToString(), r.IsActive, r.DisplayOrder, now, null });
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimulatorDummyRecords");
        }
    }
}
