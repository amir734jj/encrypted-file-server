using System.Security.Claims;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

/// <summary>
/// Creates a virtual encrypted filesystem per authenticated FTP user.
/// Directory structure: / → DataSource dirs → encrypted files (served decrypted).
/// </summary>
public sealed class EncryptedFileSystemProvider(IServiceScopeFactory scopeFactory) : IFileSystemClassFactory
{
    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var scope = scopeFactory.CreateScope();

        if (accountInformation.FtpUser.IsAnonymous())
        {
            return Task.FromResult<IUnixFileSystem>(new EncryptedUnixFileSystem(scope, userId: null));
        }

        var userId = accountInformation.FtpUser.GetUserId();
        return Task.FromResult<IUnixFileSystem>(new EncryptedUnixFileSystem(scope, userId));
    }
}