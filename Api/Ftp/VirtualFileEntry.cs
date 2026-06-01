using Api.Data.Entities;
using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

/// <summary>
/// Virtual file entry backed by an encrypted file.
/// </summary>
public sealed class VirtualFileEntry(EncryptedFile encryptedFile, string decryptedName) : IUnixFileEntry
{
    public EncryptedFile EncryptedFile => encryptedFile;
    public string DecryptedName => decryptedName;
    public string Name => decryptedName;
    public IUnixPermissions Permissions => new VirtualPermissions();
    public DateTimeOffset? LastWriteTime => encryptedFile.UpdatedAt ?? encryptedFile.CreatedAt;
    public DateTimeOffset? CreatedTime => encryptedFile.CreatedAt;
    public long NumberOfLinks => 1;
    public string Owner => "owner";
    public string Group => "group";
    public long Size => encryptedFile.OriginalFileSize;
}