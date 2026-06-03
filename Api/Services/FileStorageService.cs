using System.IO.Compression;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services.Backend;
using Api.Utilities;
using EfCoreRepository.Interfaces;
using Shared.Interfaces;
using Shared.Models;
using EfCoreRepository.Extensions;

namespace Api.Services;

public sealed class FileStorageService(
    IEfRepository repository,
    IEncryptionProviderFactory encryptionFactory,
    IBackendStorageProviderFactory storageFactory) : IFileStorageService
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();

    public async Task<EncryptedFile> StoreFileAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType, Stream content)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId && d.UserId == userId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
        {
            throw new UnauthorizedAccessException("Data source does not belong to the user.");
        }

        var ds = dataSources.First();
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var isNone = ds.Backend.EncryptionMethod == EncryptionMethod.None;
        var compress = ds.Backend.UseCompression;

        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var fileId = Guid.NewGuid();
        var connection = ds.ToBackendConnectionInfo();
        var relativePath = isNone ? fileName : $"{fileId}.enc";

        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var (destinationStream, storagePath) = await storage.OpenWriteAsync(connection, relativePath);

        byte[] iv;
        long originalSize = 0;
        var counter = new CountingStream(destinationStream);
        await using (counter)
        {
            var (cryptoStream, fileIv) = encryption.CreateEncryptingStream(counter, masterKey);
            iv = fileIv;
            await using (cryptoStream)
            {
                if (compress)
                {
                    await using var brotli = new BrotliStream(cryptoStream, CompressionLevel.Optimal);
                    originalSize = await CopyAndCountAsync(content, brotli);
                }
                else
                {
                    originalSize = await CopyAndCountAsync(content, cryptoStream);
                }
            }
        }

        var entity = await FileDal.Save(new EncryptedFile
        {
            Id = fileId,
            UserId = userId,
            DataSourceId = dataSourceId,
            OriginalFileName = encryption.EncryptString(fileName, masterKey, iv),
            StoragePath = storagePath,
            ContentType = contentType is not null ? encryption.EncryptString(contentType, masterKey, iv) : null,
            OriginalFileSize = originalSize,
            StoredFileSize = counter.BytesWritten,
            EncryptionMethod = ds.Backend.EncryptionMethod,
            IsCompressed = compress,
            IvBase64 = Convert.ToBase64String(iv),
            CreatedAt = DateTimeOffset.UtcNow
        });

        return entity;
    }

    public async Task<StreamingWriteHandle> OpenWriteStreamAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId && d.UserId == userId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
        {
            throw new UnauthorizedAccessException("Data source does not belong to the user.");
        }

        var ds = dataSources.First();
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var isNone = ds.Backend.EncryptionMethod == EncryptionMethod.None;
        var compress = ds.Backend.UseCompression;

        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var fileId = Guid.NewGuid();
        var connection = ds.ToBackendConnectionInfo();
        var relativePath = isNone ? fileName : $"{fileId}.enc";

        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var (destinationStream, storagePath) = await storage.OpenWriteAsync(connection, relativePath);
        var counter = new CountingStream(destinationStream);
        var (cryptoStream, iv) = encryption.CreateEncryptingStream(counter, masterKey);

        // If compression is enabled, wrap the crypto stream with Brotli.
        // BrotliStream disposes its inner stream by default, so StreamingWriteHandle
        // disposing the outermost stream cascades correctly.
        Stream writeStream = compress
            ? new BrotliStream(cryptoStream, CompressionLevel.Optimal)
            : cryptoStream;

        return new StreamingWriteHandle(writeStream, counter, async bytesWritten =>
        {
            var entity = await FileDal.Save(new EncryptedFile
            {
                Id = fileId,
                UserId = userId,
                DataSourceId = dataSourceId,
                OriginalFileName = encryption.EncryptString(fileName, masterKey, iv),
                StoragePath = storagePath,
                ContentType = contentType is not null ? encryption.EncryptString(contentType, masterKey, iv) : null,
                OriginalFileSize = bytesWritten,
                StoredFileSize = counter.BytesWritten,
                EncryptionMethod = ds.Backend.EncryptionMethod,
                IsCompressed = compress,
                IvBase64 = Convert.ToBase64String(iv),
                CreatedAt = DateTimeOffset.UtcNow
            });
            return entity;
        });
    }

    public async Task<Stream> OpenDecryptedStreamAsync(EncryptedFile file)
    {
        var ds = await GetDataSource(file.DataSourceId);
        var method = file.EncryptionMethod ?? ds.Backend.EncryptionMethod;
        var encryption = encryptionFactory.GetProvider(method);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var iv = Convert.FromBase64String(file.IvBase64);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var fileStream = await storage.OpenReadAsync(connection, file.StoragePath);
        Stream decrypted = encryption.CreateDecryptingStream(fileStream, masterKey, iv);
        return file.IsCompressed
            ? new BrotliStream(decrypted, CompressionMode.Decompress)
            : decrypted;
    }

    public async Task<Stream> OpenRawStreamAsync(EncryptedFile file)
    {
        var ds = await GetDataSource(file.DataSourceId);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        return await storage.OpenReadAsync(connection, file.StoragePath);
    }

    public async Task<bool> DeleteFileAsync(EncryptedFile file)
    {
        try
        {
            var ds = await GetDataSource(file.DataSourceId);
            var connection = ds.ToBackendConnectionInfo();
            var storage = storageFactory.GetProvider(ds.Backend.Protocol);
            await storage.DeleteAsync(connection, file.StoragePath);
        }
        catch (InvalidOperationException) { /* data source already deleted */ }

        await FileDal.Delete(file.Id);
        return true;
    }

    public async Task<EncryptedFile> DecryptFileAsync(EncryptedFile file)
    {
        var ds = await GetDataSource(file.DataSourceId);
        var currentMethod = file.EncryptionMethod ?? ds.Backend.EncryptionMethod;
        if (currentMethod == EncryptionMethod.None)
        {
            return file; // already decrypted
        }

        var oldEncryption = encryptionFactory.GetProvider(currentMethod);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var iv = Convert.FromBase64String(file.IvBase64);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);

        // Decrypt original file name / content type for the plaintext file
        var originalFileName = oldEncryption.DecryptString(file.OriginalFileName, masterKey, iv);
        var originalContentType = file.ContentType is not null
            ? oldEncryption.DecryptString(file.ContentType, masterKey, iv) : null;

        // Open read stream (encrypted on backend) → decrypt → write to new plaintext file
        var newFileId = Guid.NewGuid();
        var newRelativePath = originalFileName; // store under original name when unencrypted
        var noneEncryption = encryptionFactory.GetProvider(EncryptionMethod.None);
        var (newIv, newStoragePath, storedSize) = await PipeTransformAsync(
            storage, connection, file.StoragePath,
            oldEncryption, masterKey, iv,
            noneEncryption, masterKey,
            newRelativePath);

        // Update DB first so we never lose track of data if delete fails
        var oldStoragePath = file.StoragePath;
        file.StoragePath = newStoragePath;
        file.OriginalFileName = noneEncryption.EncryptString(originalFileName, masterKey, newIv);
        file.ContentType = originalContentType is not null
            ? noneEncryption.EncryptString(originalContentType, masterKey, newIv) : null;
        file.IvBase64 = Convert.ToBase64String(newIv);
        file.EncryptionMethod = EncryptionMethod.None;
        file.StoredFileSize = storedSize;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        var updated = await FileDal.Update(file.Id, (Action<EncryptedFile>)(existing =>
        {
            existing.StoragePath = file.StoragePath;
            existing.OriginalFileName = file.OriginalFileName;
            existing.ContentType = file.ContentType;
            existing.IvBase64 = file.IvBase64;
            existing.EncryptionMethod = file.EncryptionMethod;
            existing.StoredFileSize = file.StoredFileSize;
            existing.UpdatedAt = file.UpdatedAt;
        }));

        // Safe to delete old blob now — DB already points to new one
        await storage.DeleteAsync(connection, oldStoragePath);

        return updated;
    }

    public async Task<EncryptedFile> ReEncryptFileAsync(EncryptedFile file, EncryptionMethod newMethod)
    {
        var ds = await GetDataSource(file.DataSourceId);
        var currentMethod = file.EncryptionMethod ?? ds.Backend.EncryptionMethod;
        if (currentMethod == newMethod)
        {
            return file; // already using that method
        }

        var oldEncryption = encryptionFactory.GetProvider(currentMethod);
        var newEncryption = encryptionFactory.GetProvider(newMethod);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var iv = Convert.FromBase64String(file.IvBase64);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);

        var originalFileName = oldEncryption.DecryptString(file.OriginalFileName, masterKey, iv);
        var originalContentType = file.ContentType is not null
            ? oldEncryption.DecryptString(file.ContentType, masterKey, iv) : null;

        var newFileId = Guid.NewGuid();
        var isNewNone = newMethod == EncryptionMethod.None;
        var newRelativePath = isNewNone ? originalFileName : $"{newFileId}.enc";

        var (newIv, newStoragePath, storedSize) = await PipeTransformAsync(
            storage, connection, file.StoragePath,
            oldEncryption, masterKey, iv,
            newEncryption, masterKey,
            newRelativePath);

        // Update DB first so we never lose track of data if delete fails
        var oldStoragePath = file.StoragePath;
        file.StoragePath = newStoragePath;
        file.OriginalFileName = newEncryption.EncryptString(originalFileName, masterKey, newIv);
        file.ContentType = originalContentType is not null
            ? newEncryption.EncryptString(originalContentType, masterKey, newIv) : null;
        file.IvBase64 = Convert.ToBase64String(newIv);
        file.EncryptionMethod = newMethod;
        file.StoredFileSize = storedSize;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        var updated = await FileDal.Update(file.Id, (Action<EncryptedFile>)(existing =>
        {
            existing.StoragePath = file.StoragePath;
            existing.OriginalFileName = file.OriginalFileName;
            existing.ContentType = file.ContentType;
            existing.IvBase64 = file.IvBase64;
            existing.EncryptionMethod = file.EncryptionMethod;
            existing.StoredFileSize = file.StoredFileSize;
            existing.UpdatedAt = file.UpdatedAt;
        }));

        // Safe to delete old blob now — DB already points to new one
        await storage.DeleteAsync(connection, oldStoragePath);

        return updated;
    }

    public async Task<EncryptedFile> CompressFileAsync(EncryptedFile file)
    {
        if (file.IsCompressed)
        {
            return file; // already compressed
        }

        return await RewriteWithCompressionChangeAsync(file, compress: true);
    }

    public async Task<EncryptedFile> DecompressFileAsync(EncryptedFile file)
    {
        if (!file.IsCompressed)
        {
            return file; // already uncompressed
        }

        return await RewriteWithCompressionChangeAsync(file, compress: false);
    }

    /// <summary>
    /// Rewrites a file's backend blob with or without compression.
    /// Flow: read → decrypt → (decompress if needed) → (compress if needed) → encrypt → write new blob.
    /// </summary>
    private async Task<EncryptedFile> RewriteWithCompressionChangeAsync(EncryptedFile file, bool compress)
    {
        var ds = await GetDataSource(file.DataSourceId);
        var method = file.EncryptionMethod ?? ds.Backend.EncryptionMethod;
        var encryption = encryptionFactory.GetProvider(method);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var oldIv = Convert.FromBase64String(file.IvBase64);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);

        var isNone = method == EncryptionMethod.None;
        var originalFileName = encryption.DecryptString(file.OriginalFileName, masterKey, oldIv);
        var originalContentType = file.ContentType is not null
            ? encryption.DecryptString(file.ContentType, masterKey, oldIv) : null;

        var newFileId = Guid.NewGuid();
        var newRelativePath = isNone ? originalFileName : $"{newFileId}.enc";

        // Read the fully decrypted (and decompressed) content via OpenDecryptedStreamAsync
        await using var plainStream = await OpenDecryptedStreamAsync(file);

        // Write to new blob: compress (optional) → encrypt → backend
        var (destStream, newStoragePath) = await storage.OpenWriteAsync(connection, newRelativePath);
        var counter = new CountingStream(destStream);
        var (cryptoStream, newIv) = encryption.CreateEncryptingStream(counter, masterKey);
        await using (counter)
        await using (cryptoStream)
        {
            if (compress)
            {
                await using var brotli = new BrotliStream(cryptoStream, CompressionLevel.Optimal);
                await plainStream.CopyToAsync(brotli);
            }
            else
            {
                await plainStream.CopyToAsync(cryptoStream);
            }
        }

        // Update DB first
        var oldStoragePath = file.StoragePath;
        file.StoragePath = newStoragePath;
        file.OriginalFileName = encryption.EncryptString(originalFileName, masterKey, newIv);
        file.ContentType = originalContentType is not null
            ? encryption.EncryptString(originalContentType, masterKey, newIv) : null;
        file.IvBase64 = Convert.ToBase64String(newIv);
        file.IsCompressed = compress;
        file.StoredFileSize = counter.BytesWritten;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        var updated = await FileDal.Update(file.Id, (Action<EncryptedFile>)(existing =>
        {
            existing.StoragePath = file.StoragePath;
            existing.OriginalFileName = file.OriginalFileName;
            existing.ContentType = file.ContentType;
            existing.IvBase64 = file.IvBase64;
            existing.IsCompressed = file.IsCompressed;
            existing.StoredFileSize = file.StoredFileSize;
            existing.UpdatedAt = file.UpdatedAt;
        }));

        await storage.DeleteAsync(connection, oldStoragePath);
        return updated;
    }

    /// <summary>
    /// Streams data from an existing backend blob through a decrypt→encrypt pipe into a new blob.
    /// Never buffers the entire file in memory.
    /// </summary>
    private static async Task<(byte[] newIv, string newStoragePath, long storedSize)> PipeTransformAsync(
        IBackendStorageProvider storage,
        BackendConnectionInfo connection,
        string oldStoragePath,
        IEncryptionProvider oldEncryption, byte[] masterKey, byte[] oldIv,
        IEncryptionProvider newEncryption, byte[] newMasterKey,
        string newRelativePath)
    {
        await using var sourceStream = await storage.OpenReadAsync(connection, oldStoragePath);
        var decryptedStream = oldEncryption.CreateDecryptingStream(sourceStream, masterKey, oldIv);
        await using (decryptedStream)
        {
            var (destStream, newStoragePath) = await storage.OpenWriteAsync(connection, newRelativePath);
            var counter = new CountingStream(destStream);
            var (cryptoStream, newIv) = newEncryption.CreateEncryptingStream(counter, newMasterKey);
            await using (counter)
            await using (cryptoStream)
            {
                await decryptedStream.CopyToAsync(cryptoStream);
            }
            return (newIv, newStoragePath, counter.BytesWritten);
        }
    }

    private async Task<DataSource> GetDataSource(Guid dataSourceId)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
        {
            throw new InvalidOperationException("Data source not found.");
        }

        return dataSources.First();
    }

    private static async Task<long> CopyAndCountAsync(Stream source, Stream destination, int bufferSize = 81920)
    {
        var buffer = new byte[bufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read));
            total += read;
        }
        return total;
    }
}
