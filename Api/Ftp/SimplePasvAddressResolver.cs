using System.Net;
using System.Net.Sockets;
using FubarDev.FtpServer;

namespace Api.Ftp;

public sealed class SimplePasvAddressResolver(int minPort, int maxPort, IPAddress? publicAddress) : IPasvAddressResolver
{
    public Task<PasvListenerOptions> GetOptionsAsync(
        IFtpConnection connection, AddressFamily? addressFamily, CancellationToken cancellationToken)
    {
        var ip = publicAddress ?? connection.LocalEndPoint.Address;
        return Task.FromResult(new PasvListenerOptions(minPort, maxPort, ip));
    }
}
