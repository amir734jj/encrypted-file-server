using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

public sealed class ReadOnlyAccess : IAccessMode
{
    public bool Read => true;
    public bool Write => false;
    public bool Execute => false;
}