using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <summary>
    /// The simulator now uses the stored dummy data exactly as it is: the UniquePerRun switch (which tagged names and changed phone
    /// digits) is gone, and the DND digit-sum rule is off by default so only the stored DND numbers decide. A DndDigitSumRule row the
    /// admin already changed away from the starter "true" is left alone.
    /// </summary>
    public partial class SimulatorUseStoredDataAsIs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [SimulatorDummyRecords] WHERE [Category] = N'Setting' AND [Name] = N'UniquePerRun';");
            migrationBuilder.Sql("UPDATE [SimulatorDummyRecords] SET [Value] = N'false', [UpdatedAt] = SYSUTCDATETIME() WHERE [Category] = N'Setting' AND [Name] = N'DndDigitSumRule' AND [Value] = N'true' AND [UpdatedAt] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [SimulatorDummyRecords] SET [Value] = N'true' WHERE [Category] = N'Setting' AND [Name] = N'DndDigitSumRule' AND [Value] = N'false';");
            migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM [SimulatorDummyRecords] WHERE [Category] = N'Setting' AND [Name] = N'UniquePerRun') INSERT INTO [SimulatorDummyRecords] ([Id], [Category], [Name], [Value], [IsActive], [DisplayOrder], [CreatedAt]) VALUES (NEWID(), N'Setting', N'UniquePerRun', N'true', 1, 1000, SYSUTCDATETIME());");
        }
    }
}
