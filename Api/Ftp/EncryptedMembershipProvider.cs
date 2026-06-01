using Api.Data.Entities;
using EfCoreRepository.Interfaces;
using FubarDev.FtpServer.AccountManagement;
using Microsoft.AspNetCore.Identity;
using Shared.Models;
using System.Security.Claims;

namespace Api.Ftp;

public sealed class EncryptedMembershipProvider(IServiceScopeFactory scopeFactory) : IMembershipProvider
{
    public async Task<MemberValidationResult> ValidateUserAsync(string username, string password)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEfRepository>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            var hasAnonymous = await repository.For<DataSource>().Any(
                filterExprs: [d => d.Frontends.Any(f => f.Type == FrontendType.Ftp && f.AllowAnonymous)]);
            if (!hasAnonymous)
            {
                return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
            }

            var anonIdentity = new ClaimsIdentity(
                [new Claim("ftp:anonymous", "true"), new Claim(ClaimTypes.Name, "anonymous")],
                "ftp-anonymous");
            return new MemberValidationResult(MemberValidationStatus.Anonymous,
                new ClaimsPrincipal(anonIdentity));
        }

        var tickets = (await repository.For<AccessTicket>().GetAll(
            filterExprs: [t => t.Username == username && t.Password == password && t.ExpiresAt > DateTimeOffset.UtcNow],
            project: t => t,
            maxResults: 1)).ToList();

        if (tickets.Count == 0)
        {
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

        var ticket = tickets.First();

        // Reject if the ticket owner's account is disabled
        var ticketOwner = await userManager.FindByIdAsync(ticket.UserId.ToString());
        if (ticketOwner is null || !ticketOwner.IsActive)
        {
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

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