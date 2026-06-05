using System.IO.Compression;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services.Backend;
using Shared.Models;

namespace Api.Services;

/// <summary>
/// File storage service that uses the backend FTP/SFTP server as the source of truth.
/// Files are stored with their original names. The file content format is:
///   [IV bytes][encrypted(possibly_compressed(plaintext))]
/// When encryption is None, no IV is prepended.
/// </summary>
public sealed class FileStorageService(
    IEncryptionProviderFactory encryptionFactory,
    IBackendStorageProviderFactory storageFactory) : IFileStorageService
{
    public async Task StoreFileAsync(DataSource ds, string relativePath, string? contentType, Stream content)
    {
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);

        var (destinationStream, _) = await storage.OpenWriteAsync(connection, relativePath);
        await using (destinationStream)
        {
            var (cryptoStream, iv) = encryption.CreateEncryptingStream(destinationStream, masterKey);

            // Prepend IV to the file (before any encrypted content)
            if (iv.Length > 0)
            {
                await destinationStream.WriteAsync(iv);
            }

            await using (cryptoStream)
            {
                if (ds.Backend.UseCompression)
                {
                    await using var brotli = new BrotliStream(cryptoStream, CompressionLevel.Optimal);
                    await content.CopyToAsync(brotli);
                }
                else
                {
                    await content.CopyToAsync(cryptoStream);
                }
            }
        }
    }

    public async Task<StreamingWriteHandle> OpenWriteStreamAsync(DataSource ds, string relativePath, string? contentType)
    {
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);

        var (destinationStream, _) = await storage.OpenWriteAsync(connection, relativePath);
        var (cryptoStream, iv) = encryption.CreateEncryptingStream(destinationStream, masterKey);

        // Prepend IV to the file
        if (iv.Length > 0)
        {
            await destinationStream.WriteAsync(iv);
        }

        Stream writeStream = ds.Backend.UseCompression
            ? new BrotliStream(cryptoStream, CompressionLevel.Optimal)
            : cryptoStream;

        return new StreamingWriteHandle(writeStream, destinationStream);
    }

    public async Task<Stream> OpenDecryptedStreamAsync(DataSource ds, string relativePath)
    {
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);

        var fileStream = await storage.OpenReadAsync(connection, connection.ResolveStoragePath(relativePath));

        // Read the embedded IV from the start of the file
        var ivSize = GetIvSize(ds.Backend.EncryptionMethod);
        byte[] iv;
        if (ivSize > 0)
        {
            iv = new byte[ivSize];
            await fileStream.ReadExactlyAsync(iv);
        }
        else
        {
            iv = [];
        }

        Stream decrypted = encryption.CreateDecryptingStream(fileStream, masterKey, iv);

        if (!ds.Backend.UseCompression)
            return decrypted;

        // Buffer the decrypted content and attempt Brotli decompression.
        // Falls back to raw content for files placed directly on the backend
        // (not uploaded through the app) that aren't actually compressed.
        var buffer = new MemoryStream();
        await decrypted.CopyToAsync(buffer);
        await decrypted.DisposeAsync();
        buffer.Position = 0;

        try
        {
            var decompressed = new MemoryStream();
            await using (var brotli = new BrotliStream(buffer, CompressionMode.Decompress))
            {
                await brotli.CopyToAsync(decompressed);
            }
            decompressed.Position = 0;
            return decompressed;
        }
        catch (InvalidOperationException)
        {
            buffer.Position = 0;
            return buffer;
        }
    }

    public async Task<Stream> OpenRawStreamAsync(DataSource ds, string relativePath)
    {
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        return await storage.OpenReadAsync(connection, connection.ResolveStoragePath(relativePath));
    }

    public async Task<bool> DeleteFileAsync(DataSource ds, string relativePath)
    {
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        return await storage.DeleteAsync(connection, connection.ResolveStoragePath(relativePath));
    }

    public async Task<List<BackendFileEntry>> ListFilesAsync(DataSource ds, CancellationToken ct = default)
    {
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var files = await storage.ListFilesAsync(connection, ct);

        var basePath = connection.BasePath?.TrimEnd('/');
        var basePrefix = string.IsNullOrEmpty(basePath) ? null : basePath + "/";
        return files.Select(f =>
        {
            var path = f.path;
            if (basePrefix != null && path.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[basePrefix.Length..];
            }
            path = path.TrimStart('/');
            return new BackendFileEntry(path, f.size, f.modified);
        }).Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
    }

    public async Task<long> GetDecompressedSizeAsync(DataSource ds, string relativePath, CancellationToken ct = default)
    {
        if (!ds.Backend.UseCompression)
        {
            // No compression -- stored size is close to original size (minus IV/tag overhead)
            var connection = ds.ToBackendConnectionInfo();
            var storage = storageFactory.GetProvider(ds.Backend.Protocol);
            var files = await storage.ListFilesAsync(connection, ct);
            var storagePath = connection.ResolveStoragePath(relativePath);
            var match = files.FirstOrDefault(f =>
                f.path.Equals(storagePath, StringComparison.OrdinalIgnoreCase));
            return match.size;
        }

        // Stream-decompress with a small buffer to count original bytes
        await using var stream = await OpenDecryptedStreamAsync(ds, relativePath);
        var buffer = new byte[65536]; // 64KB buffer -- constant memory regardless of file size
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
        }
        return total;
    }

    public async Task<string> RenameFileAsync(DataSource ds, string oldRelativePath, string newRelativePath)
    {
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        return await storage.RenameAsync(connection,
            connection.ResolveStoragePath(oldRelativePath), newRelativePath);
    }

    public async Task<bool> ExistsAsync(DataSource ds, string relativePath, CancellationToken ct = default)
    {
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        return await storage.ExistsAsync(connection, connection.ResolveStoragePath(relativePath));
    }

    private static int GetIvSize(EncryptionMethod method) => method switch
    {
        EncryptionMethod.None => 0,
        EncryptionMethod.AesCtr256 => 16,
        EncryptionMethod.AesGcm256 => 12,
        EncryptionMethod.ChaCha20Poly1305 => 12,
        _ => throw new ArgumentException($"Unknown encryption method: {method}")
    };
}
