using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAiConfigAndAiTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The AI provider is platform-wide from here on. Before the per-tenant table goes, the one real configuration
            // that exists - Confianza IT Solutions' - becomes the platform's AiProviders settings. Secret values are copied
            // as the ciphertext they already are: the tenant table and the AppSettings table are protected with the same
            // Data Protection purpose, so nothing is ever decrypted in SQL. Only values the tenant actually set are copied.
            migrationBuilder.Sql(@"
DECLARE @tenantId uniqueidentifier = (
    SELECT TOP 1 c.TenantId
    FROM TenantAiProviderConfigs c
    JOIN Tenants t ON t.Id = c.TenantId
    WHERE t.Name = N'Confianza IT Solutions'
    ORDER BY c.UpdatedAtUtc DESC);

IF @tenantId IS NOT NULL
BEGIN
    ;WITH src AS (
        SELECT k.[Key], k.Val, k.IsSecret
        FROM TenantAiProviderConfigs c
        CROSS APPLY (VALUES
            (N'AiProviders:Provider',               CAST(c.Provider AS nvarchar(max)),            CAST(0 AS bit)),
            (N'AiProviders:EmbeddingProvider',      CAST(c.EmbeddingProvider AS nvarchar(max)),   CAST(0 AS bit)),
            (N'AiProviders:Anthropic:ApiKey',       CAST(c.AnthropicApiKey AS nvarchar(max)),     CAST(1 AS bit)),
            (N'AiProviders:Anthropic:Model',        CAST(c.AnthropicModel AS nvarchar(max)),      CAST(0 AS bit)),
            (N'AiProviders:Anthropic:ApiVersion',   CAST(c.AnthropicApiVersion AS nvarchar(max)), CAST(0 AS bit)),
            (N'AiProviders:Anthropic:BaseUrl',      CAST(c.AnthropicBaseUrl AS nvarchar(max)),    CAST(0 AS bit)),
            (N'AiProviders:OpenAI:ApiKey',          CAST(c.OpenAiApiKey AS nvarchar(max)),        CAST(1 AS bit)),
            (N'AiProviders:OpenAI:ChatModel',       CAST(c.OpenAiChatModel AS nvarchar(max)),     CAST(0 AS bit)),
            (N'AiProviders:OpenAI:EmbeddingModel',  CAST(c.OpenAiEmbeddingModel AS nvarchar(max)),CAST(0 AS bit)),
            (N'AiProviders:OpenAI:BaseUrl',         CAST(c.OpenAiBaseUrl AS nvarchar(max)),       CAST(0 AS bit)),
            (N'AiProviders:Google:ApiKey',          CAST(c.GoogleApiKey AS nvarchar(max)),        CAST(1 AS bit)),
            (N'AiProviders:Google:ChatModel',       CAST(c.GoogleChatModel AS nvarchar(max)),     CAST(0 AS bit)),
            (N'AiProviders:Google:EmbeddingModel',  CAST(c.GoogleEmbeddingModel AS nvarchar(max)),CAST(0 AS bit)),
            (N'AiProviders:Google:BaseUrl',         CAST(c.GoogleBaseUrl AS nvarchar(max)),       CAST(0 AS bit))
        ) AS k([Key], Val, IsSecret)
        WHERE c.TenantId = @tenantId AND k.Val IS NOT NULL AND k.Val <> N''
    )
    MERGE AppSettings AS d
    USING src AS s ON d.[Key] = s.[Key]
    WHEN MATCHED THEN
        UPDATE SET d.[Value] = s.Val, d.IsSecret = s.IsSecret, d.UpdatedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT ([Key], [Value], IsSecret, UpdatedAtUtc) VALUES (s.[Key], s.Val, s.IsSecret, SYSUTCDATETIME());
END");

            migrationBuilder.DropTable(
                name: "TenantAiProviderConfigs");

            migrationBuilder.CreateTable(
                name: "AiTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    DenialReason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreditsBefore = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreditsConsumed = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreditsAfter = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreditsRefunded = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    CompletionTokens = table.Column<int>(type: "int", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiTransactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiTransactions_TenantId_Operation_ReferenceId",
                table: "AiTransactions",
                columns: new[] { "TenantId", "Operation", "ReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiTransactions_TenantId_RequestedAtUtc",
                table: "AiTransactions",
                columns: new[] { "TenantId", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiTransactions");

            migrationBuilder.CreateTable(
                name: "TenantAiProviderConfigs",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnthropicApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AnthropicApiVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnthropicBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnthropicModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmbeddingProvider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GoogleApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GoogleBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GoogleChatModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GoogleEmbeddingModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenAiApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OpenAiBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenAiChatModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenAiEmbeddingModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAiProviderConfigs", x => x.TenantId);
                });
        }
    }
}
