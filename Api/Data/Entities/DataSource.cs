using Api.Interfaces;
using Shared.Contracts;

namespace Api.Data.Entities;

public sealed class DataSource : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EncryptionMethod { get; set; } = "aes-ctr-256";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string BackendFtpHost { get; set; } = string.Empty;
    public int BackendFtpPort { get; set; } = 21;
    public string BackendFtpUsername { get; set; } = string.Empty;
    public string BackendFtpPassword { get; set; } = string.Empty;
    public string BackendFtpBasePath { get; set; } = "/";
    public bool BackendFtpUseSsl { get; set; }

    public bool FrontendFtpEnabled { get; set; }
    public string? FrontendFtpPassword { get; set; }
    public bool FrontendFtpAllowAnonymous { get; set; }

    public bool FrontendHttpEnabled { get; set; }
    public string? FrontendHttpPassword { get; set; }
    public bool FrontendHttpAllowAnonymous { get; set; }
}
