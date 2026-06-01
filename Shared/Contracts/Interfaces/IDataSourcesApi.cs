using Refit;
using Shared.Models;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IDataSourcesApi
{
    [Get("/api/datasources")]
    Task<List<DataSourceDto>> GetAllAsync();

    [Post("/api/datasources")]
    Task<DataSourceDto> CreateAsync([Body] CreateDataSourceRequest request);

    [Put("/api/datasources/{id}")]
    Task UpdateAsync(Guid id, [Body] UpdateDataSourceRequest request);

    [Delete("/api/datasources/{id}")]
    Task DeleteAsync(Guid id);

    [Post("/api/datasources/{id}/decrypt")]
    Task<BulkOperationResult> DecryptAllAsync(Guid id);

    [Post("/api/datasources/{id}/reencrypt")]
    Task<BulkOperationResult> ReEncryptAllAsync(Guid id, [Query] EncryptionMethod method);
}
