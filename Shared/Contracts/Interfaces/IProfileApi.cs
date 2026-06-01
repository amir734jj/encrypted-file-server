using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface IProfileApi
{
    [Put("/api/profile")]
    Task UpdateAsync([Body] UpdateProfileRequest request);

    [Post("/api/profile/change-password")]
    Task ChangePasswordAsync([Body] ChangePasswordRequest request);
}
