using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentStudioRender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RenderJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Progress = table.Column<int>(type: "int", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AspectRatio = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    VoiceGender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Renderer = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsSimulated = table.Column<bool>(type: "bit", nullable: false),
                    IsAiVoice = table.Column<bool>(type: "bit", nullable: false),
                    ExternalRenderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OutputStorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OutputBytes = table.Column<long>(type: "bigint", nullable: false),
                    OutputSeconds = table.Column<double>(type: "float", nullable: false),
                    AudioKeysJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FlaggedScenes = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    RenderActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VoiceActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelRequested = table.Column<bool>(type: "bit", nullable: false),
                    PollFailures = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenderJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RenderJobs_Status_TenantId",
                table: "RenderJobs",
                columns: new[] { "Status", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_RenderJobs_TenantId_VersionId_CreatedAt",
                table: "RenderJobs",
                columns: new[] { "TenantId", "VersionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RenderJobs");
        }
    }
}
