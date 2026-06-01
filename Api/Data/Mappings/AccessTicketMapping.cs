using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Data.Mappings;

public sealed class AccessTicketMapping : IEntityTypeConfiguration<AccessTicket>
{
    public void Configure(EntityTypeBuilder<AccessTicket> builder)
    {
        builder.Property(t => t.Username).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Password).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.Username).IsUnique();
        builder.HasIndex(t => t.ExpiresAt);
        builder.HasOne(t => t.User)
               .WithMany()
               .HasForeignKey(t => t.UserId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
