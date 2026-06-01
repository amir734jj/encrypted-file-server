using Shared.Interfaces;

namespace Api.Services;

/// <summary>
/// Frontend data source that exposes files via HTTP REST endpoints (FilesController).
/// This is always running as part of the ASP.NET pipeline — no explicit start/stop needed.
/// </summary>
public sealed class HttpFrontendDataSource : IFrontendDataSource
{
    public string SourceKey => "http";
    public string DisplayName => "HTTP File Server";
    public bool IsRunning => true; // always running with the web host

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
