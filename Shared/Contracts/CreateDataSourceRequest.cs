namespace Shared.Contracts;

public record CreateDataSourceRequest(
    string Name,
    string EncryptionMethod = "aes-ctr-256",
    string BackendFtpHost = "",
    int BackendFtpPort = 21,
    string BackendFtpUsername = "",
    string BackendFtpPassword = "",
    string BackendFtpBasePath = "/",
    bool BackendFtpUseSsl = false,
    bool FrontendFtpEnabled = false,
    bool FrontendFtpAllowAnonymous = false,
    bool FrontendHttpEnabled = false,
    bool FrontendHttpAllowAnonymous = false);
