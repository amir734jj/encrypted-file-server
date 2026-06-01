using Api.Sftp;
using Shared.Interfaces;

namespace Api.Services.Frontend;

/// <summary>
/// Frontend data source that exposes files via the built-in SFTP server.
/// </summary>
public sealed class SftpFrontendDataSource(EncryptedSftpServer sftpServer) : IFrontendDataSource
{
    public string SourceKey => "sftp";
    public string DisplayName => "SFTP Server";
    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        sftpServer.Start();
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        sftpServer.Stop();
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (IsRunning)
            sftpServer.Stop();
        return ValueTask.CompletedTask;
    }
}
