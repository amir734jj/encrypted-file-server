using Api.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/global-config")]
[Authorize(Roles = Roles.Admin)]
public sealed class GlobalConfigController(IGlobalConfigService configService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var config = await configService.GetAsync();
        return Ok(config);
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] GlobalConfigModel config)
    {
        await configService.SaveAsync(config);
        return NoContent();
    }
}
