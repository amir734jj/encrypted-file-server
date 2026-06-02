using System.Collections.Concurrent;
using Api.Extensions;
using Api.Interfaces;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public sealed class RemoteImportController(
    IRemoteImportService remoteImport,
    IDataSourceService dataSourceService) : ControllerBase
{
    private static readonly ConcurrentDictionary<Guid, BulkOperationProgress> RunningImports = new();

    private Guid CurrentUserId => User.GetUserId();

    [HttpPost("remote/browse")]
    public async Task<IActionResult> Browse([FromBody] RemoteBrowseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Connection.Host))
            return BadRequest("Host is required.");

        try
        {
            var result = await remoteImport.BrowseAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to connect: {ex.Message}");
        }
    }

    [HttpPost("datasources/{dataSourceId:guid}/import")]
    public async Task<IActionResult> Import(Guid dataSourceId, [FromBody] RemoteImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Connection.Host))
            return BadRequest("Host is required.");

        if (await dataSourceService.GetByIdAsync(dataSourceId, CurrentUserId) is null)
            return NotFound();

        if (!RunningImports.TryAdd(dataSourceId, new BulkOperationProgress("Import", 0, 0)))
            return Conflict("An import is already running for this data source.");

        try
        {
            var result = await remoteImport.ImportAsync(
                CurrentUserId, dataSourceId, request,
                progress => RunningImports[dataSourceId] = progress,
                ct);

            return Ok(result);
        }
        finally
        {
            RunningImports.TryRemove(dataSourceId, out _);
        }
    }

    [HttpGet("datasources/{dataSourceId:guid}/import/progress")]
    public IActionResult GetProgress(Guid dataSourceId)
    {
        return RunningImports.TryGetValue(dataSourceId, out var progress)
            ? Ok(progress)
            : NoContent();
    }
}
