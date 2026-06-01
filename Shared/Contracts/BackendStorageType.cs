namespace Shared.Contracts;

/// <summary>
/// Where encrypted files are physically stored on the backend.
/// </summary>
public enum BackendStorageType
{
    FtpClient = 0,
    SftpClient = 1,
}
