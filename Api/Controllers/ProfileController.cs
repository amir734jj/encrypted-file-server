using Api.Data.Entities;
using Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize]
public sealed class ProfileController(UserManager<User> users) : ControllerBase
{
    private Guid CurrentUserId => User.GetUserId();

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateProfileRequest req)
    {
        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null)
        {
            return NotFound();
        }

        user.DisplayName = req.DisplayName?.Trim();
        await users.UpdateAsync(user);
        return Ok();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        if (req.NewPassword != req.NewPasswordConfirm)
        {
            return BadRequest(new { message = "New passwords do not match." });
        }

        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null)
        {
            return NotFound();
        }

        var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(new { message = result.Errors.FirstOrDefault()?.Description ?? "Failed to change password." });
        }

        return Ok();
    }
}
