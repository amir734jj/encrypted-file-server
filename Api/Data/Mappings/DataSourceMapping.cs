using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Data.Mappings;

public sealed class DataSourceMapping : IEntityTypeConfiguration<DataSource>
{
    public void Configure(EntityTypeBuilder<DataSource> builder)
    {
        builder.Property(d => d.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(d => new { d.UserId, d.Name }).IsUnique();
        builder.HasOne(d => d.User)
               .WithMany()
               .HasForeignKey(d => d.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        // Backend FTP config
        builder.Property(d => d.BackendFtpHost).HasMaxLength(500).IsRequired();
        builder.Property(d => d.BackendFtpUsername).HasMaxLength(200);
        builder.Property(d => d.BackendFtpPassword).HasMaxLength(500);
        builder.Property(d => d.BackendFtpBasePath).HasMaxLength(500);

        // Frontend passwords (auto-generated)
        builder.Property(d => d.FrontendFtpPassword).HasMaxLength(200);
        builder.Property(d => d.FrontendHttpPassword).HasMaxLength(200);
    }
}
