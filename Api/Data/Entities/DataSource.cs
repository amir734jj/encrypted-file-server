using Api.Interfaces;
using Shared.Contracts;

namespace Api.Data.Entities;

public sealed class DataSource : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Backend (FTP client to remote FTP server) ──
    public string BackendFtpHost { get; set; } = string.Empty;
    public int BackendFtpPort { get; set; } = 21;
    public string BackendFtpUsername { get; set; } = string.Empty;
    public string BackendFtpPassword { get; set; } = string.Empty;
    public string BackendFtpBasePath { get; set; } = "/";
    public bool BackendFtpUseSsl { get; set; }

    // ── Frontend: FTP Server ──
    public bool FrontendFtpEnabled { get; set; }
    public string? FrontendFtpPassword { get; set; }
    public bool FrontendFtpAllowAnonymous { get; set; }

    // ── Frontend: HTTP File Server ──
    public bool FrontendHttpEnabled { get; set; }
    public string? FrontendHttpPassword { get; set; }
    public bool FrontendHttpAllowAnonymous { get; set; }
}
