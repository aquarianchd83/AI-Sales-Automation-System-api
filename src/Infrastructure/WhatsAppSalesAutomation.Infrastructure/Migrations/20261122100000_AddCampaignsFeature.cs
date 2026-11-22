using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <summary>
    /// Campaigns became its own plan feature (it used to ride on WhatsApp messaging). Every plan that already includes WhatsApp messaging keeps
    /// campaigns, so nobody loses what they had; platform admins can untick it per plan afterwards. No schema change - features are stored as text.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261122100000_AddCampaignsFeature")]
    public partial class AddCampaignsFeature : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO PlanFeatures (Id, PlanId, Feature, CreatedAt)
SELECT NEWID(), pf.PlanId, 'Campaigns', SYSUTCDATETIME()
FROM PlanFeatures pf
WHERE pf.Feature = 'WhatsAppMessaging'
  AND NOT EXISTS (SELECT 1 FROM PlanFeatures x WHERE x.PlanId = pf.PlanId AND x.Feature = 'Campaigns');");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM PlanFeatures WHERE Feature = 'Campaigns';");
        }
    }
}
