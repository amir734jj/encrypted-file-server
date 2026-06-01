using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

public sealed class VirtualPermissions : IUnixPermissions
{
    public IAccessMode User => new FullAccess();
    public IAccessMode Group => new ReadOnlyAccess();
    public IAccessMode Other => new ReadOnlyAccess();
}