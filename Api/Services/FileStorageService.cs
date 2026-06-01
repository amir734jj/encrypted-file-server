using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services.Backend;
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
            throw new UnauthorizedAccessException("Data source does not belong to the user.");

        var ds = dataSources.First();
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var isNone = ds.Backend.EncryptionMethod == EncryptionMethod.None;

        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var fileId = Guid.NewGuid();
        var connection = ds.ToBackendConnectionInfo();
        var relativePath = isNone ? fileName : $"{fileId}.enc";

        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var (destinationStream, storagePath) = await storage.OpenWriteAsync(connection, relativePath);

        byte[] iv;
        await using (destinationStream)
        {
            var (cryptoStream, fileIv) = encryption.CreateEncryptingStream(destinationStream, masterKey);
            iv = fileIv;
            await using (cryptoStream)
            {
                await content.CopyToAsync(cryptoStream);
            }
        }

        long originalSize = content.CanSeek ? content.Length : 0;

        var entity = await FileDal.Save(new EncryptedFile
        {
            Id = fileId,
            UserId = userId,
            DataSourceId = dataSourceId,
            OriginalFileName = encryption.EncryptString(fileName, masterKey, iv),
            StoragePath = storagePath,
            ContentType = contentType is not null ? encryption.EncryptString(contentType, masterKey, iv) : null,
            OriginalFileSize = originalSize,
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
            throw new UnauthorizedAccessException("Data source does not belong to the user.");

        var ds = dataSources.First();
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var isNone = ds.Backend.EncryptionMethod == EncryptionMethod.None;

        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var fileId = Guid.NewGuid();
        var connection = ds.ToBackendConnectionInfo();
        var relativePath = isNone ? fileName : $"{fileId}.enc";

        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var (destinationStream, storagePath) = await storage.OpenWriteAsync(connection, relativePath);
        var (cryptoStream, iv) = encryption.CreateEncryptingStream(destinationStream, masterKey);

        return new StreamingWriteHandle(cryptoStream, destinationStream, async bytesWritten =>
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
                IvBase64 = Convert.ToBase64String(iv),
                CreatedAt = DateTimeOffset.UtcNow
            });
            return entity;
        });
    }

    public async Task<Stream> OpenDecryptedStreamAsync(EncryptedFile file)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == file.DataSourceId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
            throw new InvalidOperationException("Data source not found.");

        var ds = dataSources.First();
        var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);
        var iv = Convert.FromBase64String(file.IvBase64);
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        var fileStream = await storage.OpenReadAsync(connection, file.StoragePath);
        return encryption.CreateDecryptingStream(fileStream, masterKey, iv);
    }

    public async Task<Stream> OpenRawStreamAsync(EncryptedFile file)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == file.DataSourceId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
            throw new InvalidOperationException("Data source not found.");

        var ds = dataSources.First();
        var connection = ds.ToBackendConnectionInfo();
        var storage = storageFactory.GetProvider(ds.Backend.Protocol);
        return await storage.OpenReadAsync(connection, file.StoragePath);
    }

    public async Task<bool> DeleteFileAsync(EncryptedFile file)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == file.DataSourceId],
            project: d => d,
            maxResults: 1)).ToList();

        if (dataSources.Count > 0)
        {
            var ds = dataSources.First();
            var connection = ds.ToBackendConnectionInfo();
            var storage = storageFactory.GetProvider(ds.Backend.Protocol);
            await storage.DeleteAsync(connection, file.StoragePath);
        }

        await FileDal.Delete(file.Id);
        return true;
    }
}
