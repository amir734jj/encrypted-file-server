using System.IO.Compression;
using System.Security.Cryptography;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services.Backend;
using Shared.Models;

namespace Api.Services;

/// <summary>
/// File storage service that uses the backend FTP/SFTP server as the source of truth.
/// Files are stored with optionally encrypted names. The file content format is:
///   [IV bytes][encrypted(possibly_compressed(plaintext))]
/// When encryption is None, no IV is prepended and names stay in plaintext.
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

        var storageName = EncryptPath(ds, relativePath, masterKey);

        var (destinationStream, _) = await storage.OpenWriteAsync(connection, storageName);
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

        var storageName = EncryptPath(ds, relativePath, masterKey);

        var (destinationStream, _) = await storage.OpenWriteAsync(connection, storageName);
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

        var storageName = EncryptPath(ds, relativePath, masterKey);
        var fileStream = await storage.OpenReadAsync(connection, connection.ResolveStoragePath(storageName));

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
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var storageName = EncryptPath(ds, relativePath, masterKey);
        return await storage.OpenReadAsync(connection, connection.ResolveStoragePath(storageName));
    }

    public async Task<bool> DeleteFileAsync(DataSource ds, string relativePath)
    {
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var storageName = EncryptPath(ds, relativePath, masterKey);
        return await storage.DeleteAsync(connection, connection.ResolveStoragePath(storageName));
    }

    public async Task<List<BackendFileEntry>> ListFilesAsync(DataSource ds, CancellationToken ct = default)
    {
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var files = await storage.ListFilesAsync(connection, ct);

        var basePath = connection.BasePath?.TrimEnd('/');
        var basePrefix = string.IsNullOrEmpty(basePath) ? null : basePath + "/";
        return files
            .Where(f => basePrefix == null || f.path.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var path = basePrefix != null ? f.path[basePrefix.Length..] : f.path;
                path = path.TrimStart('/');
                // Decrypt path segments if filename encryption is enabled
                path = DecryptPath(ds, path, masterKey);
                return new BackendFileEntry(path, f.size, f.modified);
            }).Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
    }

    public async Task<long> GetDecompressedSizeAsync(DataSource ds, string relativePath, CancellationToken ct = default)
    {
        if (!ds.Backend.UseCompression)
        {
            // No compression -- stored size is close to original size (minus IV/tag overhead)
            var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
            var connection = ds.ToBackendConnectionInfo();
            var storage = storageFactory.GetProvider(ds.Backend.Protocol);
            var files = await storage.ListFilesAsync(connection, ct);
            var storageName = EncryptPath(ds, relativePath, masterKey);
            var storagePath = connection.ResolveStoragePath(storageName);
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
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var oldStorageName = EncryptPath(ds, oldRelativePath, masterKey);
        var newStorageName = EncryptPath(ds, newRelativePath, masterKey);
        return await storage.RenameAsync(connection,
            connection.ResolveStoragePath(oldStorageName), newStorageName);
    }

    public async Task<bool> ExistsAsync(DataSource ds, string relativePath, CancellationToken ct = default)
    {
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var storageName = EncryptPath(ds, relativePath, masterKey);
        return await storage.ExistsAsync(connection, connection.ResolveStoragePath(storageName));
    }

    private static int GetIvSize(EncryptionMethod method) => method switch
    {
        EncryptionMethod.None => 0,
        EncryptionMethod.AesCtr256 => 16,
        EncryptionMethod.AesGcm256 => 12,
        EncryptionMethod.ChaCha20Poly1305 => 12,
        _ => throw new ArgumentException($"Unknown encryption method: {method}")
    };

    // ── Filename encryption helpers ──────────────────────────────────────

    /// <summary>
    /// Encrypts each segment of a relative path using a deterministic IV derived
    /// from the master key. Returns a filesystem-safe base64url-encoded path.
    /// </summary>
    private string EncryptPath(DataSource ds, string relativePath, byte[] masterKey)
    {
        if (ds.Backend.EncryptionMethod == EncryptionMethod.None)
            return relativePath;

        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var iv = DeriveFilenameIv(masterKey, GetIvSize(ds.Backend.EncryptionMethod));

        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.IsNullOrEmpty(segments[i])) continue;
            var encrypted = encryption.EncryptString(segments[i], masterKey, iv);
            segments[i] = Base64UrlEncode(encrypted);
        }
        return string.Join("/", segments);
    }

    /// <summary>
    /// Decrypts each segment of a path returned by the backend, reversing EncryptPath.
    /// Segments that fail to decrypt are returned as-is (e.g. files placed manually).
    /// </summary>
    private string DecryptPath(DataSource ds, string encryptedPath, byte[] masterKey)
    {
        if (ds.Backend.EncryptionMethod == EncryptionMethod.None)
            return encryptedPath;

        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var iv = DeriveFilenameIv(masterKey, GetIvSize(ds.Backend.EncryptionMethod));

        var segments = encryptedPath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.IsNullOrEmpty(segments[i])) continue;
            try
            {
                var base64 = Base64UrlDecode(segments[i]);
                segments[i] = encryption.DecryptString(base64, masterKey, iv);
            }
            catch
            {
                // Not an encrypted segment — leave as-is
            }
        }
        return string.Join("/", segments);
    }

    /// <summary>
    /// Derives a deterministic IV/nonce for filename encryption from the master key.
    /// Uses HMAC-SHA256 with a fixed context string, truncated to the required size.
    /// </summary>
    private static byte[] DeriveFilenameIv(byte[] masterKey, int ivSize)
    {
        var hash = HMACSHA256.HashData(masterKey, "encrypted-file-server:filename-iv"u8);
        return hash[..ivSize];
    }

    private static string Base64UrlEncode(string base64)
    {
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Base64UrlDecode(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return s;
    }
}
