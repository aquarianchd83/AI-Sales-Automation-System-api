using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcePassageEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcePassageEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PassageHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Embedding = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmbeddedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcePassageEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcePassageEmbeddings_TenantId_PassageHash_Provider_Model",
                table: "SourcePassageEmbeddings",
                columns: new[] { "TenantId", "PassageHash", "Provider", "Model" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourcePassageEmbeddings");
        }
    }
}
