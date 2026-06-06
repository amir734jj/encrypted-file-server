using Shared.Contracts;

namespace Shared.Interfaces;

/// <summary>
/// Abstraction for the physical storage backend where encrypted file bytes are persisted.
/// Each call receives connection info so the provider can target different remote servers per data source.
/// </summary>
public interface IBackendStorageProvider
{
    /// <summary>Unique key identifying this provider (e.g. "ftp-client", "sftp-client").</summary>
    string ProviderKey { get; }

    /// <summary>The backend storage type this provider handles.</summary>
    BackendStorageType StorageType { get; }

    /// <summary>Opens a writable stream to a storage location on the remote backend.</summary>
    Task<(Stream stream, string storagePath)> OpenWriteAsync(BackendConnectionInfo connection, string relativePath, CancellationToken ct = default);

    /// <summary>Opens a readable stream from an existing storage location on the remote backend.</summary>
    Task<Stream> OpenReadAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);

    /// <summary>Deletes a file from the remote backend.</summary>
    Task<bool> DeleteAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);

    /// <summary>Deletes a directory from the remote backend.</summary>
    Task<bool> DeleteDirectoryAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);

    /// <summary>Checks whether a stored file exists on the remote backend.</summary>
    Task<bool> ExistsAsync(BackendConnectionInfo connection, string storagePath, CancellationToken ct = default);

    /// <summary>Renames/moves a file on the remote backend. Returns the new full storage path.</summary>
    Task<string> RenameAsync(BackendConnectionInfo connection, string oldStoragePath, string newRelativePath, CancellationToken ct = default);

    /// <summary>Lists all files recursively under the base path on the remote backend.</summary>
    Task<List<(string path, long size, DateTimeOffset? modified)>> ListFilesAsync(BackendConnectionInfo connection, CancellationToken ct = default);
}