using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IGlobalConfigApi
{
    [Get("/api/global-config")]
    Task<GlobalConfigModel> GetAsync();

    [Put("/api/global-config")]
    Task SaveAsync([Body] GlobalConfigModel config);
}
