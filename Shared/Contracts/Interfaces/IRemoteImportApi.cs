using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IRemoteImportApi
{
    [Post("/api/remote/browse")]
    Task<RemoteBrowseResponse> BrowseAsync([Body] RemoteBrowseRequest request);

    [Post("/api/datasources/{dataSourceId}/import")]
    Task ImportAsync(Guid dataSourceId, [Body] RemoteImportRequest request);

    [Get("/api/datasources/{dataSourceId}/import/progress")]
    Task<ApiResponse<string>> GetImportProgressRawAsync(Guid dataSourceId);
}
