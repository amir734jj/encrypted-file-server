using System.Collections.Concurrent;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;
using Shared.Models;

namespace Api.Controllers;

[ApiController]
[Route("api/datasources")]
[Authorize]
public sealed class DataSourcesController(
    IDataSourceService dataSourceService,
    IEfRepository repository,
    IFileStorageService fileStorage) : ControllerBase
{
    private static readonly ConcurrentDictionary<Guid, BulkOperationProgress> RunningOps = new();

    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();
    private Guid CurrentUserId => User.GetUserId();

    [HttpGet("{id:guid}/progress")]
    public IActionResult GetProgress(Guid id)
    {
        return RunningOps.TryGetValue(id, out var progress)
            ? Ok(progress)
            : NoContent();
    }

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

    [HttpPost("{id:guid}/decrypt")]
    public async Task<IActionResult> DecryptAll(Guid id)
    {
        if (await dataSourceService.GetByIdAsync(id, CurrentUserId) is null)
        {
            return NotFound();
        }

        if (!RunningOps.TryAdd(id, new BulkOperationProgress("Decrypt", 0, 0)))
        {
            return Conflict("A bulk operation is already running for this data source.");
        }

        try
        {
            var files = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == id && f.UserId == CurrentUserId],
                project: f => f)).ToList();

            RunningOps[id] = new BulkOperationProgress("Decrypt", files.Count, 0);

            var processed = 0;
            foreach (var file in files)
            {
                try
                {
                    await fileStorage.DecryptFileAsync(file);
                    processed++;
                    RunningOps[id] = new BulkOperationProgress("Decrypt", files.Count, processed);
                }
                catch { /* skip files that fail */ }
            }

            return Ok(new BulkOperationResult(files.Count, processed));
        }
        finally
        {
            RunningOps.TryRemove(id, out _);
        }
    }

    [HttpPost("{id:guid}/reencrypt")]
    public async Task<IActionResult> ReEncryptAll(Guid id, [FromQuery] EncryptionMethod method)
    {
        if (await dataSourceService.GetByIdAsync(id, CurrentUserId) is null)
        {
            return NotFound();
        }

        if (!RunningOps.TryAdd(id, new BulkOperationProgress("Re-encrypt", 0, 0)))
        {
            return Conflict("A bulk operation is already running for this data source.");
        }

        try
        {
            var files = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == id && f.UserId == CurrentUserId],
                project: f => f)).ToList();

            RunningOps[id] = new BulkOperationProgress("Re-encrypt", files.Count, 0);

            var processed = 0;
            foreach (var file in files)
            {
                try
                {
                    await fileStorage.ReEncryptFileAsync(file, method);
                    processed++;
                    RunningOps[id] = new BulkOperationProgress("Re-encrypt", files.Count, processed);
                }
                catch { /* skip files that fail */ }
            }

            return Ok(new BulkOperationResult(files.Count, processed));
        }
        finally
        {
            RunningOps.TryRemove(id, out _);
        }
    }
}
