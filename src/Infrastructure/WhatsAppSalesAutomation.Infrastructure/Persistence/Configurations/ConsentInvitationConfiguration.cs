using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class ConsentInvitationConfiguration : IEntityTypeConfiguration<ConsentInvitation>
{
    public void Configure(EntityTypeBuilder<ConsentInvitation> builder)
    {
        builder.ToTable("ConsentInvitations");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.IdempotencyKey).IsRequired().HasMaxLength(120);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.ToPhoneE164).IsRequired().HasMaxLength(20);
        builder.Property(i => i.Body).IsRequired().HasMaxLength(1000);
        builder.Property(i => i.WhatsAppLink).IsRequired().HasMaxLength(200);
        builder.Property(i => i.ProviderMessageId).HasMaxLength(200);
        builder.Property(i => i.FailureReason).HasMaxLength(1000);
        builder.Property(i => i.ConsentMessageId).HasMaxLength(200);
        builder.Property(i => i.CorrelationId).HasMaxLength(100);

        // The consent page: the link's secret is stored only as a hash; the decision is its own state, apart from the SMS's.
        builder.Property(i => i.TokenHash).HasMaxLength(64);
        builder.Property(i => i.DecisionStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.DecisionSource).HasMaxLength(40);
        builder.Property(i => i.DecisionConsentText).HasMaxLength(1000);
        builder.Property(i => i.DecisionIpHash).HasMaxLength(64);
        builder.Property(i => i.DecisionUserAgent).HasMaxLength(200);
        builder.Property(i => i.WhatsAppInitiation).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.WhatsAppInitiationError).HasMaxLength(1000);
        builder.Property(i => i.RowVersion).IsRowVersion();

        // The final guarantee that one customer is invited once per campaign, however many retries, reruns or racing instances there are.
        builder.HasIndex(i => i.IdempotencyKey).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.CustomerId });
        builder.HasIndex(i => i.ProviderMessageId);
        builder.HasIndex(i => new { i.Status, i.NextAttemptAt });

        // Looking a link up by its hash must be fast, and two links can never share one.
        builder.HasIndex(i => i.TokenHash).IsUnique().HasFilter("[TokenHash] IS NOT NULL");
        builder.HasIndex(i => new { i.TenantId, i.DecisionStatus });
    }
}

public class TenantConsentPageConfiguration : IEntityTypeConfiguration<TenantConsentPage>
{
    public void Configure(EntityTypeBuilder<TenantConsentPage> builder)
    {
        builder.ToTable("TenantConsentPages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.BusinessName).HasMaxLength(ConsentPageRules.MaxBusinessName);
        builder.Property(p => p.ProductDescription).HasMaxLength(ConsentPageRules.MaxDescription);
        builder.Property(p => p.AdditionalInfo).HasMaxLength(ConsentPageRules.MaxAdditionalInfo);
        builder.Property(p => p.ConsentStatement).HasMaxLength(ConsentPageRules.MaxStatement);
        builder.Property(p => p.PrivacyPolicyUrl).HasMaxLength(ConsentPageRules.MaxUrl);
        builder.Property(p => p.TermsUrl).HasMaxLength(ConsentPageRules.MaxUrl);

        // One page per tenant.
        builder.HasIndex(p => p.TenantId).IsUnique();
    }
}

public class ConsentPageVersionConfiguration : IEntityTypeConfiguration<ConsentPageVersion>
{
    public void Configure(EntityTypeBuilder<ConsentPageVersion> builder)
    {
        builder.ToTable("ConsentPageVersions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.BusinessName).IsRequired().HasMaxLength(ConsentPageRules.MaxBusinessName);
        builder.Property(v => v.ProductLabel).HasMaxLength(200);
        builder.Property(v => v.ProductDescription).IsRequired().HasMaxLength(ConsentPageRules.MaxDescription);
        builder.Property(v => v.AdditionalInfo).HasMaxLength(ConsentPageRules.MaxAdditionalInfo);
        // The statement after [Business Name] / [Product/Service] are filled in, which can be longer than the template.
        builder.Property(v => v.ConsentStatement).IsRequired().HasMaxLength(1000);
        builder.Property(v => v.PrivacyPolicyUrl).HasMaxLength(ConsentPageRules.MaxUrl);
        builder.Property(v => v.TermsUrl).HasMaxLength(ConsentPageRules.MaxUrl);

        // Version numbers are per tenant and never reused; this also stops two simultaneous publishes from both taking the same number.
        builder.HasIndex(v => new { v.TenantId, v.Version }).IsUnique();
    }
}
