using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261005120000_AddMediaThumbnails")]
    public partial class AddMediaThumbnails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThumbnailStorageKey",
                table: "MediaAssets",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailStorageKey",
                table: "PlatformMediaAssets",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ThumbnailStorageKey", table: "MediaAssets");
            migrationBuilder.DropColumn(name: "ThumbnailStorageKey", table: "PlatformMediaAssets");
        }
    }
}
