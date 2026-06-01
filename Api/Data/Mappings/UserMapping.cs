using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Data.Mappings;

public sealed class UserMapping : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.MasterKeyBase64)
            .HasMaxLength(100)
            .IsRequired()
            .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Throw);

        builder.Property(u => u.DisplayName).HasMaxLength(200);
    }
}
