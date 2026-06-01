using FubarDev.FtpServer.FileSystem;

namespace Api.Ftp;

public sealed class FullAccess : IAccessMode
{
    public bool Read => true;
    public bool Write => true;
    public bool Execute => true;
}