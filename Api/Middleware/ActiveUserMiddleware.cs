using Api.Data.Entities;
using Api.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Middleware;

public sealed class ActiveUserMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public async Task InvokeAsync(HttpContext context, UserManager<User> userManager, IMemoryCache cache)
    {
        if (context.User.Identity is not { IsAuthenticated: true })
        {
            await next(context);
            return;
        }

        var userId = context.User.GetUserId().ToString();
        var cacheKey = $"active_user:{userId}";

        if (!cache.TryGetValue(cacheKey, out bool isActive))
        {
            var user = await userManager.FindByIdAsync(userId);
            isActive = user is { IsActive: true };
            cache.Set(cacheKey, isActive, CacheDuration);
        }

        if (!isActive)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Account has been deactivated." });
            return;
        }

        await next(context);
    }
}
