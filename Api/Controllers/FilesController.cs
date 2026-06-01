using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/files")]
[Authorize]
public sealed class FilesController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    UserManager<User> users) : ControllerBase
{
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private Guid CurrentUserId => User.GetUserId();

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid dataSourceId)
    {
        var files = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == CurrentUserId],
            orderBy: f => f.OriginalFileName,
            project: f => new FileEntryDto(
                f.Id, f.DataSourceId, f.OriginalFileName,
                f.ContentType, f.OriginalFileSize, f.CreatedAt, f.UpdatedAt))).ToList();

        return Ok(files);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(500_000_000)] // 500 MB
    public async Task<IActionResult> Upload([FromQuery] Guid dataSourceId, IFormFile file)
    {
        var dsExists = await DataSourceDal.Any(filterExprs: [d => d.Id == dataSourceId && d.UserId == CurrentUserId]);
        if (!dsExists)
            return NotFound("Data source not found.");

        await using var stream = file.OpenReadStream();
        var entity = await fileStorage.StoreFileAsync(
            CurrentUserId, dataSourceId, file.FileName, file.ContentType, stream);

        return Ok(new FileEntryDto(
            entity.Id, entity.DataSourceId, entity.OriginalFileName,
            entity.ContentType, entity.OriginalFileSize, entity.CreatedAt, entity.UpdatedAt));
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id)
    {
        var files = (await FileDal.GetAll(
            filterExprs: [f => f.Id == id && f.UserId == CurrentUserId],
            project: f => f,
            maxResults: 1)).ToList();
        if (files.Count == 0) return NotFound();

        var file = files.First();
        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null) return NotFound();

        var masterKey = Convert.FromBase64String(user.MasterKeyBase64);
        var stream = await fileStorage.OpenDecryptedStreamAsync(file, masterKey);

        return File(stream, file.ContentType ?? "application/octet-stream", file.OriginalFileName);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var files = (await FileDal.GetAll(
            filterExprs: [f => f.Id == id && f.UserId == CurrentUserId],
            project: f => f,
            maxResults: 1)).ToList();
        if (files.Count == 0) return NotFound();

        await fileStorage.DeleteFileAsync(files.First());
        return NoContent();
    }
}
