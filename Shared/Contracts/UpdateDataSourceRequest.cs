namespace Shared.Contracts;

public record UpdateDataSourceRequest(
    string Name,
    string BackendFtpHost,
    int BackendFtpPort = 21,
    string BackendFtpUsername = "",
    string BackendFtpPassword = "",
    string BackendFtpBasePath = "/",
    bool BackendFtpUseSsl = false,
    bool FrontendFtpEnabled = false,
    bool FrontendFtpAllowAnonymous = false,
    bool FrontendHttpEnabled = false,
    bool FrontendHttpAllowAnonymous = false);
