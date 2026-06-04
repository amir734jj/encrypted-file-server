using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IFilesApi
{
    [Get("/api/files")]
    Task<DirectoryListingDto> GetAllAsync([Query] Guid dataSourceId, [Query] string path = "");

    [Multipart]
    [Post("/api/files/upload")]
    Task<FileEntryDto> UploadAsync([Query] Guid dataSourceId, [Query] string path, [AliasAs("file")] StreamPart file);

    [Delete("/api/files")]
    Task DeleteAsync([Query] Guid dataSourceId, [Query] string path);

    [Delete("/api/files/folder")]
    Task DeleteFolderAsync([Query] Guid dataSourceId, [Query] string path);

    [Post("/api/files/move-folder")]
    Task MoveFolderAsync([Query] Guid dataSourceId, [Query] string sourcePath, [Query] string destinationPath);
}
