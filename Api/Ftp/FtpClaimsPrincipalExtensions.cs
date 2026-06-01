using System.Security.Claims;

namespace Api.Ftp;

public static class FtpClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static bool IsAnonymous(this ClaimsPrincipal principal)
        => principal.FindFirstValue("ftp:anonymous") == "true";
}