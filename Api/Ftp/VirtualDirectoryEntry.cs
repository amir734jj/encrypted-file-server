using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

/// <summary>
/// Virtual directory representing either the root or a data source folder.
/// </summary>
public sealed class VirtualDirectoryEntry(string name, Guid? dataSourceId, string? virtualPath) : IUnixDirectoryEntry
{
    public Guid? DataSourceId => dataSourceId;
    public string? VirtualPath => virtualPath;
    public string Name => name;
    public IUnixPermissions Permissions => new VirtualPermissions();
    public DateTimeOffset? LastWriteTime => DateTimeOffset.UtcNow;
    public DateTimeOffset? CreatedTime => DateTimeOffset.UtcNow;
    public long NumberOfLinks => 1;
    public string Owner => "owner";
    public string Group => "group";
    public bool IsDeletable => dataSourceId is not null;
    public bool IsRoot => dataSourceId is null && name == "/";
}