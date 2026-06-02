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
    IDataSourceService dataSourceService,
    IServiceScopeFactory scopeFactory,
    ILogger<RemoteImportController> logger) : ControllerBase
{
    private static readonly ConcurrentDictionary<Guid, BulkOperationProgress> RunningImports = new();
    private static readonly ConcurrentDictionary<Guid, RemoteImportResult> CompletedImports = new();

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
    public async Task<IActionResult> Import(Guid dataSourceId, [FromBody] RemoteImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Connection.Host))
            return BadRequest("Host is required.");

        if (await dataSourceService.GetByIdAsync(dataSourceId, CurrentUserId) is null)
            return NotFound();

        if (!RunningImports.TryAdd(dataSourceId, new BulkOperationProgress("Import", 0, 0)))
            return Conflict("An import is already running for this data source.");

        CompletedImports.TryRemove(dataSourceId, out _);

        var userId = CurrentUserId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IRemoteImportService>();

                var result = await svc.ImportAsync(
                    userId, dataSourceId, request,
                    progress => RunningImports[dataSourceId] = progress);

                CompletedImports[dataSourceId] = result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background import failed for DS {DsId}", dataSourceId);
                CompletedImports[dataSourceId] = new RemoteImportResult(0, 0, 1, [ex.Message]);
            }
            finally
            {
                RunningImports.TryRemove(dataSourceId, out _);
            }
        });

        return Accepted();
    }

    [HttpGet("datasources/{dataSourceId:guid}/import/progress")]
    public IActionResult GetProgress(Guid dataSourceId)
    {
        if (CompletedImports.TryRemove(dataSourceId, out var result))
            return Ok(result);

        return RunningImports.TryGetValue(dataSourceId, out var progress)
            ? Ok(progress)
            : NoContent();
    }
}
