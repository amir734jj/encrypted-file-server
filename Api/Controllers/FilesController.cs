using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;
using Shared.Interfaces;

namespace Api.Controllers;

[ApiController]
[Route("api/files")]
[Authorize]
public sealed class FilesController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    IEncryptionProviderFactory encryptionFactory,
    UserManager<User> users) : ControllerBase
{
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private Guid CurrentUserId => User.GetUserId();

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid dataSourceId, [FromQuery] string path = "")
    {
        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null) return Unauthorized();
        var masterKey = Convert.FromBase64String(user.MasterKeyBase64);

        var ds = await GetDataSource(dataSourceId);
        if (ds is null) return NotFound();
        var encryption = encryptionFactory.GetProvider(ds.EncryptionMethod);

        path = NormalizePath(path);

        var allFiles = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == CurrentUserId],
            project: f => f)).ToList();

        var decrypted = allFiles.Select(f =>
        {
            var iv = Convert.FromBase64String(f.IvBase64);
            var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);
            var contentType = f.ContentType is not null
                ? encryption.DecryptString(f.ContentType, masterKey, iv) : null;
            return (file: f, fullPath, contentType);
        }).ToList();

        var files = new List<FileEntryDto>();
        var subfolders = new Dictionary<string, (int count, long size)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, fullPath, contentType) in decrypted)
        {
            if (!fullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = fullPath[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                files.Add(new FileEntryDto(
                    file.Id, file.DataSourceId, relativePath, contentType,
                    file.OriginalFileSize, file.CreatedAt, file.UpdatedAt));
            }
            else
            {
                var folderName = relativePath[..slashIndex];
                if (subfolders.TryGetValue(folderName, out var existing))
                    subfolders[folderName] = (existing.count + 1, existing.size + file.OriginalFileSize);
                else
                    subfolders[folderName] = (1, file.OriginalFileSize);
            }
        }

        var folders = subfolders
            .Select(kvp => new FolderEntryDto(kvp.Key, kvp.Value.count, kvp.Value.size))
            .OrderBy(f => f.Name).ToList();

        return Ok(new DirectoryListingDto(path, folders, files.OrderBy(f => f.FileName).ToList()));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(500_000_000)]
    public async Task<IActionResult> Upload([FromQuery] Guid dataSourceId, [FromQuery] string path, IFormFile file)
    {
        var dsExists = await DataSourceDal.Any(filterExprs: [d => d.Id == dataSourceId && d.UserId == CurrentUserId]);
        if (!dsExists)
            return NotFound("Data source not found.");

        path = NormalizePath(path);
        var fullPath = path + file.FileName;

        await using var stream = file.OpenReadStream();
        var entity = await fileStorage.StoreFileAsync(
            CurrentUserId, dataSourceId, fullPath, file.ContentType, stream);

        var ds = await GetDataSource(dataSourceId);
        var encryption = encryptionFactory.GetProvider(ds!.EncryptionMethod);
        var iv = Convert.FromBase64String(entity.IvBase64);
        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        var masterKey = Convert.FromBase64String(user!.MasterKeyBase64);

        return Ok(new FileEntryDto(
            entity.Id, entity.DataSourceId,
            encryption.DecryptString(entity.OriginalFileName, masterKey, iv),
            entity.ContentType is not null ? encryption.DecryptString(entity.ContentType, masterKey, iv) : null,
            entity.OriginalFileSize, entity.CreatedAt, entity.UpdatedAt));
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
        var ds = await GetDataSource(file.DataSourceId);
        if (ds is null) return NotFound();
        var encryption = encryptionFactory.GetProvider(ds.EncryptionMethod);

        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null) return NotFound();

        var masterKey = Convert.FromBase64String(user.MasterKeyBase64);
        var iv = Convert.FromBase64String(file.IvBase64);
        var stream = await fileStorage.OpenDecryptedStreamAsync(file, masterKey);
        var fullPath = encryption.DecryptString(file.OriginalFileName, masterKey, iv);
        var fileName = Path.GetFileName(fullPath);
        var contentType = file.ContentType is not null
            ? encryption.DecryptString(file.ContentType, masterKey, iv)
            : "application/octet-stream";

        return File(stream, contentType, fileName);
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

    [HttpDelete("folder")]
    public async Task<IActionResult> DeleteFolder([FromQuery] Guid dataSourceId, [FromQuery] string path)
    {
        path = NormalizePath(path);
        if (string.IsNullOrEmpty(path))
            return BadRequest("Cannot delete root folder.");

        var user = await users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null) return Unauthorized();
        var masterKey = Convert.FromBase64String(user.MasterKeyBase64);

        var ds = await GetDataSource(dataSourceId);
        if (ds is null) return NotFound();
        var encryption = encryptionFactory.GetProvider(ds.EncryptionMethod);

        var allFiles = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == CurrentUserId],
            project: f => f)).ToList();

        var toDelete = allFiles.Where(f =>
        {
            var iv = Convert.FromBase64String(f.IvBase64);
            var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);
            return fullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        foreach (var file in toDelete)
            await fileStorage.DeleteFileAsync(file);

        return Ok(new { Deleted = toDelete.Count });
    }

    private async Task<DataSource?> GetDataSource(Guid id)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == id],
            project: d => d,
            maxResults: 1)).ToList();
        return dataSources.FirstOrDefault();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        path = path.Replace('\\', '/').Trim('/');
        return path.Length > 0 ? path + "/" : string.Empty;
    }
}
