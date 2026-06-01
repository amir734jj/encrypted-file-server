using Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = Roles.Admin)]
public sealed class UsersController(UserManager<User> users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var allUsers = users.Users.ToList();
        var dtos = new List<UserDto>();

        foreach (var user in allUsers)
        {
            var userRoles = await users.GetRolesAsync(user);
            dtos.Add(new UserDto(user.Id, user.Email!, user.IsActive, userRoles.ToList(), user.LastLoginAt));
        }

        return Ok(dtos);
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        user.IsActive = true;
        await users.UpdateAsync(user);
        return NoContent();
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        user.IsActive = false;
        await users.UpdateAsync(user);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        await users.DeleteAsync(user);
        return NoContent();
    }

    [HttpPost("{id:guid}/make-admin")]
    public async Task<IActionResult> MakeAdmin(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        await users.RemoveFromRoleAsync(user, Roles.User);
        await users.AddToRoleAsync(user, Roles.Admin);
        return NoContent();
    }

    [HttpPost("{id:guid}/make-user")]
    public async Task<IActionResult> MakeUser(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        await users.RemoveFromRoleAsync(user, Roles.Admin);
        await users.AddToRoleAsync(user, Roles.User);
        return NoContent();
    }
}
