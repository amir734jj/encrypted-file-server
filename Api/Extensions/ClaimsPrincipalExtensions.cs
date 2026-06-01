using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                    ?? throw new InvalidOperationException("User ID claim missing.");
        return Guid.Parse(value);
    }

    public static string GetEmail(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(JwtRegisteredClaimNames.Email)
               ?? throw new InvalidOperationException("Email claim missing.");
    }

    public static string? TryGetEmail(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(JwtRegisteredClaimNames.Email);
    }
}
