using Refit;

namespace Shared.Contracts.Interfaces;

public interface IAuthApi
{
    [Post("/api/auth/login")]
    Task<LoginResponse> LoginAsync([Body] LoginRequest request);

    [Post("/api/auth/register")]
    Task<RegisterResponse> RegisterAsync([Body] RegisterRequest request);

    [Get("/api/auth/me")]
    [Headers("Authorization: Bearer")]
    Task<MeResponse> MeAsync();
}
