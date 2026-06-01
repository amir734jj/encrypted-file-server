using Api.Data;
using Api.Data.Entities;
using FubarDev.FtpServer.AccountManagement;
using Microsoft.EntityFrameworkCore;
using Shared.Models;
using System.Security.Claims;

namespace Api.Ftp;

public sealed class EncryptedMembershipProvider(IServiceScopeFactory scopeFactory) : IMembershipProvider
{
    public async Task<MemberValidationResult> ValidateUserAsync(string username, string password)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            var hasAnonymous = await db.DataSources.AnyAsync(d => d.Frontends.Any(f => f.Type == FrontendType.Ftp && f.AllowAnonymous));
            if (!hasAnonymous)
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

            var anonIdentity = new ClaimsIdentity(
                [new Claim("ftp:anonymous", "true"), new Claim(ClaimTypes.Name, "anonymous")],
                "ftp-anonymous");
            return new MemberValidationResult(MemberValidationStatus.Anonymous,
                new ClaimsPrincipal(anonIdentity));
        }

        var ticket = await db.AccessTickets
            .FirstOrDefaultAsync(t => t.Username == username
                && t.Password == password
                && t.ExpiresAt > DateTimeOffset.UtcNow);

        if (ticket is null)
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, ticket.UserId.ToString()),
            new Claim(ClaimTypes.Name, ticket.Username),
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
