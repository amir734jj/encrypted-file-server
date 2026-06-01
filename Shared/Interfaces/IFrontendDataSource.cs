namespace Shared.Interfaces;

/// <summary>
/// Abstraction for a frontend data source — a channel through which users push/pull files.
/// Implementations: FTP server, HTTP upload/download, SFTP, WebDAV, etc.
/// Each frontend source runs as a hosted service and exposes files to users.
/// </summary>
public interface IFrontendDataSource : IAsyncDisposable
{
    /// <summary>Unique key identifying this frontend (e.g. "ftp", "http", "sftp").</summary>
    string SourceKey { get; }

    /// <summary>Human-readable display name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Whether this frontend is currently running and accepting connections.</summary>
    bool IsRunning { get; }

    /// <summary>Start accepting connections/requests.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop accepting connections/requests gracefully.</summary>
    Task StopAsync(CancellationToken ct = default);
}
