using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Models;

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

        builder.OwnsOne(d => d.Backend, b =>
        {
            b.Property(x => x.Protocol)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasColumnName("BackendProtocol");
            b.Property(x => x.Host).HasMaxLength(500).IsRequired().HasColumnName("BackendHost");
            b.Property(x => x.Port).HasColumnName("BackendPort");
            b.Property(x => x.Username).HasMaxLength(200).HasColumnName("BackendUsername");
            b.Property(x => x.Password).HasMaxLength(500).HasColumnName("BackendPassword");
            b.Property(x => x.BasePath).HasMaxLength(500).HasColumnName("BackendBasePath");
            b.Property(x => x.UseSsl).HasColumnName("BackendUseSsl");
            b.Property(x => x.EncryptionMethod)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasColumnName("EncryptionMethod");
            b.Property(x => x.MasterPassword).HasMaxLength(500).HasColumnName("BackendMasterPassword");
        });

        builder.OwnsMany(d => d.Frontends, f =>
        {
            f.WithOwner().HasForeignKey("DataSourceId");
            f.Property<int>("Id").ValueGeneratedOnAdd();
            f.HasKey("Id");
            f.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            f.ToTable("DataSourceFrontends");
        });
    }
}
