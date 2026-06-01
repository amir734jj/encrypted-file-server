using FubarDev.FtpServer;
using Shared.Interfaces;

namespace Api.Services.Frontend;

/// <summary>
/// Frontend data source that exposes files via the built-in FTP server.
/// </summary>
public sealed class FtpFrontendDataSource(IFtpServerHost ftpHost) : IFrontendDataSource
{
    public string SourceKey => "ftp";
    public string DisplayName => "FTP Server";
    public bool IsRunning { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await ftpHost.StartAsync(ct);
        IsRunning = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await ftpHost.StopAsync(ct);
        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync();
    }
}
