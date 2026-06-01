namespace Shared.Contracts;

public record DataSourceDto(
    Guid Id,
    string Name,
    long TotalFileSize,
    int FileCount,
    DateTimeOffset CreatedAt,
    string BackendFtpHost,
    int BackendFtpPort,
    string BackendFtpUsername,
    string BackendFtpBasePath,
    bool BackendFtpUseSsl,
    bool FrontendFtpEnabled,
    string? FrontendFtpPassword,
    bool FrontendFtpAllowAnonymous,
    bool FrontendHttpEnabled,
    string? FrontendHttpPassword,
    bool FrontendHttpAllowAnonymous);
