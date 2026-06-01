namespace Shared.Contracts;

public record DataSourceDto(
    Guid Id,
    string Name,
    long TotalFileSize,
    int FileCount,
    DateTimeOffset CreatedAt,

    // Backend
    string BackendFtpHost,
    int BackendFtpPort,
    string BackendFtpUsername,
    string BackendFtpBasePath,
    bool BackendFtpUseSsl,

    // Frontend: FTP
    bool FrontendFtpEnabled,
    string? FrontendFtpPassword,
    bool FrontendFtpAllowAnonymous,

    // Frontend: HTTP
    bool FrontendHttpEnabled,
    string? FrontendHttpPassword,
    bool FrontendHttpAllowAnonymous);
