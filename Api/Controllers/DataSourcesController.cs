using Api.Extensions;
using Api.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/datasources")]
[Authorize]
public sealed class DataSourcesController(
    IDataSourceService dataSourceService) : ControllerBase
{
    private Guid CurrentUserId => User.GetUserId();

    [HttpGet("{id:guid}/master-password")]
    public async Task<IActionResult> GetMasterPassword(Guid id)
    {
        var password = await dataSourceService.GetMasterPasswordAsync(id, CurrentUserId);
        return password is not null ? Ok(new MasterPasswordResponse(password)) : NotFound();
    }

    [HttpGet("{id:guid}/credentials")]
    public async Task<IActionResult> GetCredentials(Guid id)
    {
        var creds = await dataSourceService.GetCredentialsAsync(id, CurrentUserId);
        return creds is not null ? Ok(creds) : NotFound();
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return Ok(await dataSourceService.GetAllAsync(CurrentUserId));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDataSourceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest("Data source name is required.");
        }

        if (string.IsNullOrWhiteSpace(req.Backend.Host))
        {
            return BadRequest("Backend host is required.");
        }

        if (await dataSourceService.ExistsByNameAsync(CurrentUserId, req.Name))
        {
            return Conflict($"Data source '{req.Name}' already exists.");
        }

        var ds = await dataSourceService.CreateAsync(CurrentUserId, req);
        return Ok(ds);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDataSourceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest("Data source name is required.");
        }

        if (string.IsNullOrWhiteSpace(req.Backend.Host))
        {
            return BadRequest("Backend host is required.");
        }

        var updated = await dataSourceService.UpdateAsync(id, CurrentUserId, req);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await dataSourceService.DeleteAsync(id, CurrentUserId);
        return deleted ? NoContent() : NotFound();
    }
}
