using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IRemoteImportApi
{
    [Post("/api/remote/browse")]
    Task<RemoteBrowseResponse> BrowseAsync([Body] RemoteBrowseRequest request);

    [Post("/api/datasources/{dataSourceId}/import")]
    Task<RemoteImportResult> ImportAsync(Guid dataSourceId, [Body] RemoteImportRequest request);

    [Get("/api/datasources/{dataSourceId}/import/progress")]
    Task<BulkOperationProgress?> GetImportProgressAsync(Guid dataSourceId);
}
