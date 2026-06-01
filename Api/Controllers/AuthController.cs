using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Api.Data.Entities;
using Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Shared;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<User> users,
    RoleManager<Role> roles,
    IConfiguration config) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest req)
    {
        if (req.Password != req.PasswordConfirm)
        {
            return BadRequest(new[] { new { code = "PasswordMismatch", description = "Passwords do not match." } });
        }

        foreach (var role in new[] { Roles.Admin, Roles.User })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new Role { Name = role });
            }
        }

        var isFirstUser = !users.Users.Any();
        var assignedRole = isFirstUser ? Roles.Admin : Roles.User;

        var user = new User
        {
            UserName = req.Email,
            Email = req.Email,
            IsActive = isFirstUser,
        };

        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        await users.AddToRoleAsync(user, assignedRole);
        return Ok(new RegisterResponse(isFirstUser));
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest req)
    {
        var user = await users.FindByEmailAsync(req.Email);
        if (user is null || !await users.CheckPasswordAsync(user, req.Password))
        {
            return Unauthorized();
        }

        if (!user.IsActive)
        {
            return StatusCode(403, new { message = "Account is pending activation by an administrator." });
        }

        var userRoles = await users.GetRolesAsync(user);
        var role = userRoles.FirstOrDefault() ?? Roles.User;

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);

        return Ok(new LoginResponse(BuildToken(user, role), role, user.Id));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await users.FindByIdAsync(User.GetUserId().ToString());
        if (user is null)
        {
            return NotFound();
        }

        return Ok(new MeResponse(user.Email!, user.DisplayName));
    }

    [HttpPost("impersonate/{userId:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Impersonate(Guid userId)
    {
        var targetUser = await users.FindByIdAsync(userId.ToString());
        if (targetUser is null) return NotFound();

        var targetRoles = await users.GetRolesAsync(targetUser);
        var role = targetRoles.FirstOrDefault() ?? Roles.User;

        return Ok(new LoginResponse(BuildToken(targetUser, role), role, targetUser.Id));
    }

    private string BuildToken(User user, string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email!),
                new Claim(ClaimTypes.Role, role)
            ],
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
