using Api.Data.Entities;

namespace Api.Interfaces;

/// <summary>
/// File storage service that uses the backend FTP/SFTP server as the source of truth.
/// Files are stored with their original names; content is encrypted with IV prepended.
/// </summary>
public interface IFileStorageService
{
    /// <summary>Stores a file: compress (optional) -> encrypt -> prepend IV -> write to backend.</summary>
    Task StoreFileAsync(DataSource ds, string relativePath, string? contentType, Stream content);

    /// <summary>Opens a streaming write pipeline for chunk-by-chunk uploads.</summary>
    Task<StreamingWriteHandle> OpenWriteStreamAsync(DataSource ds, string relativePath, string? contentType);

    /// <summary>Opens a decrypted (and decompressed) read stream for the given file.</summary>
    Task<Stream> OpenDecryptedStreamAsync(DataSource ds, string relativePath);

    /// <summary>Opens the raw backend stream (IV + encrypted bytes) without decryption.</summary>
    Task<Stream> OpenRawStreamAsync(DataSource ds, string relativePath);

    /// <summary>Deletes a file from the backend.</summary>
    Task<bool> DeleteFileAsync(DataSource ds, string relativePath);

    /// <summary>Lists all files on the backend storage (filenames decrypted).</summary>
    Task<List<BackendFileEntry>> ListFilesAsync(DataSource ds, CancellationToken ct = default);

    /// <summary>Lists all files with their raw storage names (no filename decryption).</summary>
    Task<List<BackendFileEntry>> ListFilesRawAsync(DataSource ds, CancellationToken ct = default);

    /// <summary>
    /// Computes the original (decompressed) file size by streaming through the decrypt/decompress
    /// pipeline with a small buffer, suitable for large files without high memory usage.
    /// </summary>
    Task<long> GetDecompressedSizeAsync(DataSource ds, string relativePath, CancellationToken ct = default);

    /// <summary>Renames/moves a file on the backend.</summary>
    Task<string> RenameFileAsync(DataSource ds, string oldRelativePath, string newRelativePath);

    /// <summary>Checks whether a file exists on the backend.</summary>
    Task<bool> ExistsAsync(DataSource ds, string relativePath, CancellationToken ct = default);
}

/// <summary>
/// Represents a file as reported by the backend storage.
/// </summary>
public record BackendFileEntry(string Path, long StoredSize, DateTimeOffset? Modified);