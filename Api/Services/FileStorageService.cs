using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Shared.Interfaces;
using EfCoreRepository.Extensions;

namespace Api.Services;

public sealed class FileStorageService(
    IEfRepository repository,
    IEncryptionProvider encryption,
    IBackendStorageProvider storage,
    UserManager<User> userManager) : IFileStorageService
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();

    public async Task<EncryptedFile> StoreFileAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType, Stream content)
    {
        // Verify the data source belongs to the user and get connection info
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId && d.UserId == userId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
            throw new UnauthorizedAccessException("Data source does not belong to the user.");

        var ds = dataSources.First();
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("User not found.");

        var masterKey = Convert.FromBase64String(user.MasterKeyBase64);
        var fileId = Guid.NewGuid();
        var connection = ds.ToBackendConnectionInfo();

        var (destinationStream, storagePath) = await storage.OpenWriteAsync(connection, fileId);

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
            OriginalFileName = Path.GetFileName(fileName),
            StoragePath = storagePath,
            ContentType = contentType,
            OriginalFileSize = originalSize,
            IvBase64 = Convert.ToBase64String(iv),
            CreatedAt = DateTimeOffset.UtcNow
        });

        return entity;
    }

    public async Task<Stream> OpenDecryptedStreamAsync(EncryptedFile file, byte[] masterKey)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == file.DataSourceId],
            project: d => d,
            maxResults: 1)).ToList();
        if (dataSources.Count == 0)
            throw new InvalidOperationException("Data source not found.");

        var ds = dataSources.First();
        var iv = Convert.FromBase64String(file.IvBase64);
        var connection = ds.ToBackendConnectionInfo();
        var fileStream = await storage.OpenReadAsync(connection, file.StoragePath);
        return encryption.CreateDecryptingStream(fileStream, masterKey, iv);
    }

    public async Task<bool> DeleteFileAsync(EncryptedFile file)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == file.DataSourceId],
            project: d => d,
            maxResults: 1)).ToList();

        if (dataSources.Count > 0)
        {
            var connection = dataSources.First().ToBackendConnectionInfo();
            await storage.DeleteAsync(connection, file.StoragePath);
        }

        await FileDal.Delete(file.Id);
        return true;
    }
}
