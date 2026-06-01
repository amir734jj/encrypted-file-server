using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Data.Mappings;

public sealed class GlobalConfigMapping : IEntityTypeConfiguration<GlobalConfig>
{
    public void Configure(EntityTypeBuilder<GlobalConfig> builder)
    {
        builder.Property(c => c.Key).HasMaxLength(100);
        builder.HasIndex(c => c.Key).IsUnique();
        builder.Property(c => c.Value).HasMaxLength(500);
    }
}
