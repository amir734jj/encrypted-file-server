using Shared.Contracts;

namespace Api.Interfaces;

public interface IGlobalConfigService
{
    Task<GlobalConfigModel> GetAsync();
    Task SaveAsync(GlobalConfigModel config);
    Task InitAsync();
}
