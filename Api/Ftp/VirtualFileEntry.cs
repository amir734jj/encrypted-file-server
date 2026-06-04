using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

/// <summary>
/// Virtual file entry backed by a file on the backend storage.
/// </summary>
public sealed class VirtualFileEntry(
    Guid dataSourceId,
    string relativePath,
    string fileName,
    long size,
    DateTimeOffset? modified) : IUnixFileEntry
{
    public Guid DataSourceId => dataSourceId;
    public string RelativePath => relativePath;
    public string Name => fileName;
    public IUnixPermissions Permissions => new VirtualPermissions();
    public DateTimeOffset? LastWriteTime => modified;
    public DateTimeOffset? CreatedTime => modified;
    public long NumberOfLinks => 1;
    public string Owner => "owner";
    public string Group => "group";
    public long Size => size;
}