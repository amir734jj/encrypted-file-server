using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Data.Mappings;

public sealed class EncryptedFileMapping : IEntityTypeConfiguration<EncryptedFile>
{
    public void Configure(EntityTypeBuilder<EncryptedFile> builder)
    {
        builder.Property(f => f.OriginalFileName).HasMaxLength(2000).IsRequired();
        builder.Property(f => f.StoragePath).HasMaxLength(1000).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(500);
        builder.Property(f => f.IvBase64).HasMaxLength(50).IsRequired();

        builder.HasOne(f => f.User)
               .WithMany()
               .HasForeignKey(f => f.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.DataSource)
               .WithMany()
               .HasForeignKey(f => f.DataSourceId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.DataSourceId);
    }
}
