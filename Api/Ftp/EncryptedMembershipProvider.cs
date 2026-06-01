using Api.Data;
using Api.Data.Entities;
using FubarDev.FtpServer.AccountManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Api.Ftp;

public sealed class EncryptedMembershipProvider(IServiceScopeFactory scopeFactory) : IMembershipProvider
{
    public async Task<MemberValidationResult> ValidateUserAsync(string username, string password)
    {
        using var scope = scopeFactory.CreateScope();

        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasAnonymous = await db.DataSources.AnyAsync(d => d.FrontendFtpAllowAnonymous);
            if (!hasAnonymous)
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

            var anonIdentity = new ClaimsIdentity(
                [new Claim("ftp:anonymous", "true"), new Claim(ClaimTypes.Name, "anonymous")],
                "ftp-anonymous");
            return new MemberValidationResult(MemberValidationStatus.Anonymous,
                new ClaimsPrincipal(anonIdentity));
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(username);
        if (user is null || !user.IsActive)
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        var valid = await userManager.CheckPasswordAsync(user, password);
        if (!valid)
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email!),
            new Claim(ClaimTypes.Email, user.Email!)
        };
        var identity = new ClaimsIdentity(claims, "ftp");
        return new MemberValidationResult(MemberValidationStatus.AuthenticatedUser,
            new ClaimsPrincipal(identity));
    }
}

public static class FtpClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static bool IsAnonymous(this ClaimsPrincipal principal)
        => principal.FindFirstValue("ftp:anonymous") == "true";
}
