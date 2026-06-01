using Api.Data.Entities;

namespace Api.Interfaces;

public interface IFileStorageService
{
    Task<EncryptedFile> StoreFileAsync(Guid userId, Guid dataSourceId, string fileName, string? contentType, Stream content);
    Task<Stream> OpenDecryptedStreamAsync(EncryptedFile file, byte[] masterKey);
    Task<bool> DeleteFileAsync(EncryptedFile file);
}
