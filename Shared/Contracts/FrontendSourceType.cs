namespace Shared.Contracts;

/// <summary>
/// How users access their files (output channel).
/// </summary>
public enum FrontendSourceType
{
    FtpServer = 0,
    HttpFileServer = 1,
    // Sftp = 2,
    WebDav = 3,
}
