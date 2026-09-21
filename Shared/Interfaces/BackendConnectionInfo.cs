using Shared.Contracts;

namespace Shared.Interfaces;

/// <summary>
/// Connection details for a backend storage target (e.g. remote FTP server).
/// </summary>
public record BackendConnectionInfo(
    string Host,
    int Port,
    string Username,
    string Password,
    string BasePath,
    bool UseSsl,
    BackendStorageType Protocol = BackendStorageType.FtpClient)
{
    public string ResolveStoragePath(string relativePath)
    {
        var basePath = (BasePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var path = relativePath.Replace('\\', '/').TrimStart('/');

        return string.IsNullOrEmpty(basePath)
            ? path
            : string.IsNullOrEmpty(path) ? basePath : $"{basePath}/{path}";
    }
}