using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AnalysisServiceRegistrationConfiguration : IEntityTypeConfiguration<AnalysisServiceRegistration>
{
    public void Configure(EntityTypeBuilder<AnalysisServiceRegistration> builder)
    {
        builder.ToTable("analysis_service_registrations", table =>
        {
            table.HasCheckConstraint("CK_analysis_service_registrations_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
            table.HasCheckConstraint("CK_analysis_service_registrations_capacity", "\"Capacity\" > 0");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ServiceKey).IsRequired().HasMaxLength(128);
        builder.Property(r => r.Capacity).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.Ignore(r => r.UpdatedAt);

        // One registration per service identity; its capacity is pinned on first registration.
        builder.HasIndex(r => r.ServiceKey)
            .IsUnique()
            .HasDatabaseName("UIX_analysis_service_registrations_service_key");
    }
}
