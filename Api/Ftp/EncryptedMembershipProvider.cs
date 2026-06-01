using Api.Data.Entities;
using FubarDev.FtpServer.AccountManagement;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace Api.Ftp;

/// <summary>
/// Authenticates FTP users against ASP.NET Identity.
/// Username = email, Password = user's password.
/// The user ID is stored in the ClaimTypes.NameIdentifier claim.
/// </summary>
public sealed class EncryptedMembershipProvider(IServiceScopeFactory scopeFactory) : IMembershipProvider
{
    public async Task<MemberValidationResult> ValidateUserAsync(string username, string password)
    {
        using var scope = scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var user = await userManager.FindByEmailAsync(username);
        if (user is null || !user.IsActive)
        {
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

        var valid = await userManager.CheckPasswordAsync(user, password);
        if (!valid)
        {
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email!),
            new Claim(ClaimTypes.Email, user.Email!)
        };
        var identity = new ClaimsIdentity(claims, "ftp");
        var principal = new ClaimsPrincipal(identity);

        return new MemberValidationResult(MemberValidationStatus.AuthenticatedUser, principal);
    }
}

public static class FtpClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        return Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
