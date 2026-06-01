using Api.Data.Entities;
using Shared.Models;

namespace Api.Interfaces;

public interface IFileStorageService
{
    Task<EncryptedFile> StoreFileAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType, Stream content);
    Task<StreamingWriteHandle> OpenWriteStreamAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType);
    Task<Stream> OpenDecryptedStreamAsync(EncryptedFile file);
    Task<Stream> OpenRawStreamAsync(EncryptedFile file);
    Task<bool> DeleteFileAsync(EncryptedFile file);
    Task<EncryptedFile> DecryptFileAsync(EncryptedFile file);
    Task<EncryptedFile> ReEncryptFileAsync(EncryptedFile file, EncryptionMethod newMethod);
}