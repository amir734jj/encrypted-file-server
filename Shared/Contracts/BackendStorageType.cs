namespace Shared.Contracts;

/// <summary>
/// Where encrypted files are physically stored on the backend.
/// </summary>
public enum BackendStorageType
{
    FtpClient = 0,
    // S3 = 1,
    // AzureBlob = 2,
}
