using Api.Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<User, Role, Guid>(options)
{
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<GlobalConfig> GlobalConfigs => Set<GlobalConfig>();
    public DbSet<AccessTicket> AccessTickets => Set<AccessTicket>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
