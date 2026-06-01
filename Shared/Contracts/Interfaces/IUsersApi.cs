using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IUsersApi
{
    [Get("/api/users")]
    Task<List<UserDto>> GetAllAsync();

    [Post("/api/users/{id}/activate")]
    Task ActivateAsync(Guid id);

    [Post("/api/users/{id}/deactivate")]
    Task DeactivateAsync(Guid id);

    [Delete("/api/users/{id}")]
    Task DeleteAsync(Guid id);

    [Post("/api/users/{id}/make-admin")]
    Task MakeAdminAsync(Guid id);

    [Post("/api/users/{id}/make-user")]
    Task MakeUserAsync(Guid id);
}
