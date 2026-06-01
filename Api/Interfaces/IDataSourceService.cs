using Shared.Contracts;

namespace Api.Interfaces;

public interface IDataSourceService
{
    Task<List<DataSourceDto>> GetAllAsync(Guid userId);
    Task<DataSourceDto?> GetByIdAsync(Guid id, Guid userId);
    Task<DataSourceDto> CreateAsync(Guid userId, CreateDataSourceRequest req);
    Task<bool> UpdateAsync(Guid id, Guid userId, UpdateDataSourceRequest req);
    Task<bool> DeleteAsync(Guid id, Guid userId);
    Task<bool> ExistsByNameAsync(Guid userId, string name);
    Task<string?> GetMasterPasswordAsync(Guid id, Guid userId);
}
