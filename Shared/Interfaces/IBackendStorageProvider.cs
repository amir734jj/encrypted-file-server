namespace Shared.Interfaces;

/// <summary>
/// Abstraction for the physical storage backend where encrypted file bytes are persisted.
/// Each call receives connection info so the provider can target different remote servers per data source.
/// </summary>
public interface IBackendStorageProvider
{
    /// <summary>Unique key identifying this provider (e.g. "ftp-client", "s3").</summary>
    string ProviderKey { get; }

    /// <summary>Opens a writable stream to a storage location on the remote backend.</summary>
    Task<(Stream stream, string storagePath)> OpenWriteAsync(BackendConnectionInfo connection, string relativePath, CancellationToken ct = default);

    /// <summary>Opens a readable stream from an existing storage location on the remote backend.</summary>
    Task<Stream> OpenReadAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);

    /// <summary>Deletes a file from the remote backend.</summary>
    Task<bool> DeleteAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);

    /// <summary>Checks whether a stored file exists on the remote backend.</summary>
    Task<bool> ExistsAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);
}

/// <summary>
/// Connection details for a backend storage target (e.g. remote FTP server).
/// </summary>
public record BackendConnectionInfo(
    string Host,
    int Port,
    string Username,
    string Password,
    string BasePath,
    bool UseSsl);
