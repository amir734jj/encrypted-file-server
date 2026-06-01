using Shared.Interfaces;

namespace Api.Services.Frontend;

/// <summary>
/// Frontend data source that exposes files via WebDAV (handled by WebDavMiddleware).
/// Always running as part of the ASP.NET pipeline — no explicit start/stop needed.
/// </summary>
public sealed class WebDavFrontendDataSource : IFrontendDataSource
{
    public string SourceKey => "webdav";
    public string DisplayName => "WebDAV Server";
    public bool IsRunning => true;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
