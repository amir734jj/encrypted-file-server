using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IFilesApi
{
    [Get("/api/files")]
    Task<List<FileEntryDto>> GetAllAsync([Query] Guid dataSourceId);

    [Multipart]
    [Post("/api/files/upload")]
    Task<FileEntryDto> UploadAsync([Query] Guid dataSourceId, [AliasAs("file")] StreamPart file);

    [Delete("/api/files/{id}")]
    Task DeleteAsync(Guid id);
}
