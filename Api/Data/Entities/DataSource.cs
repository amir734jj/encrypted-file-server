using Api.Interfaces;
using Shared.Models;

namespace Api.Data.Entities;

public sealed class DataSource : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Maximum total size in bytes for all files in this data source.
    /// Null means no limit.
    /// </summary>
    public long? MaxSizeBytes { get; set; }

    public BackendConfig Backend { get; set; } = new();
    public List<FrontendConfig> Frontends { get; set; } = [];

    public FrontendConfig? GetFrontend(FrontendType type) =>
        Frontends.FirstOrDefault(f => f.Type == type);

    public bool HasFrontend(FrontendType type) =>
        Frontends.Any(f => f.Type == type);
}
