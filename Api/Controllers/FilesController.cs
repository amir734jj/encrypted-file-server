using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/files")]
[Authorize]
public sealed class FilesController(
    IEfRepository repository,
    IFileStorageService fileStorage) : ControllerBase
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private Guid CurrentUserId => User.GetUserId();
    private static readonly FileExtensionContentTypeProvider MimeMap = new();

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid dataSourceId, [FromQuery] string path = "")
    {
        var ds = await GetDataSource(dataSourceId);
        if (ds is null)
        {
            return NotFound();
        }

        path = NormalizePath(path);

        var backendFiles = await fileStorage.ListFilesAsync(ds);

        var files = new List<FileEntryDto>();
        var subfolders = new Dictionary<string, (int count, long size)>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in backendFiles)
        {
            var filePath = f.Path;

            // Directory marker (trailing "/", size -1) — register as folder
            if (filePath.EndsWith('/'))
            {
                filePath = filePath[..^1]; // strip trailing "/"
                if (!string.IsNullOrEmpty(path) &&
                    !filePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relDir = string.IsNullOrEmpty(path) ? filePath : filePath[path.Length..];
                var dirSlash = relDir.IndexOf('/');
                var dirName = dirSlash < 0 ? relDir : relDir[..dirSlash];
                if (!string.IsNullOrEmpty(dirName))
                    subfolders.TryAdd(dirName, (0, 0));
                continue;
            }

            if (!string.IsNullOrEmpty(path) &&
                !filePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = string.IsNullOrEmpty(path) ? filePath : filePath[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                MimeMap.TryGetContentType(relativePath, out var contentType);
                files.Add(new FileEntryDto(
                    dataSourceId, relativePath, contentType,
                    f.StoredSize, f.Modified));
            }
            else
            {
                var folderName = relativePath[..slashIndex];
                if (subfolders.TryGetValue(folderName, out var existing))
                {
                    subfolders[folderName] = (existing.count + 1, existing.size + f.StoredSize);
                }
                else
                {
                    subfolders[folderName] = (1, f.StoredSize);
                }
            }
        }

        var folders = subfolders
            .Select(kvp => new FolderEntryDto(kvp.Key, kvp.Value.count, kvp.Value.size))
            .OrderBy(f => f.Name).ToList();

        return Ok(new DirectoryListingDto(path, folders, files.OrderBy(f => f.FileName).ToList()));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(500_000_000)]
    public async Task<IActionResult> Upload([FromQuery] Guid dataSourceId, [FromQuery] string path = "", IFormFile? file = null)
    {
        if (file is null)
        {
            return BadRequest("No file provided.");
        }

        var ds = await GetDataSource(dataSourceId);
        if (ds is null)
        {
            return NotFound("Data source not found.");
        }

        path = NormalizePath(path);
        var fullPath = path + file.FileName;

        // Delete existing file with the same name (replace semantics)
        if (await fileStorage.ExistsAsync(ds, fullPath))
        {
            await fileStorage.DeleteFileAsync(ds, fullPath);
        }

        await using var stream = file.OpenReadStream();
        await fileStorage.StoreFileAsync(ds, fullPath, file.ContentType, stream);

        MimeMap.TryGetContentType(file.FileName, out var contentType);

        return Ok(new FileEntryDto(
            dataSourceId, file.FileName, contentType ?? file.ContentType,
            file.Length, DateTimeOffset.UtcNow));
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] Guid dataSourceId, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Path is required.");
        }

        var ds = await GetDataSource(dataSourceId);
        if (ds is null)
        {
            return NotFound();
        }

        path = NormalizePath(path).TrimEnd('/');
        if (!await fileStorage.ExistsAsync(ds, path))
        {
            return NotFound();
        }

        var stream = await fileStorage.OpenDecryptedStreamAsync(ds, path);
        var fileName = Path.GetFileName(path);
        MimeMap.TryGetContentType(fileName, out var contentType);
        contentType ??= "application/octet-stream";

        return File(stream, contentType, fileName);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] Guid dataSourceId, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Path is required.");
        }

        var ds = await GetDataSource(dataSourceId);
        if (ds is null)
        {
            return NotFound();
        }

        path = NormalizePath(path).TrimEnd('/');
        var deleted = await fileStorage.DeleteFileAsync(ds, path);
        return deleted ? NoContent() : NotFound();
    }

    [HttpDelete("folder")]
    public async Task<IActionResult> DeleteFolder([FromQuery] Guid dataSourceId, [FromQuery] string path)
    {
        path = NormalizePath(path);
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest("Cannot delete root folder.");
        }

        var ds = await GetDataSource(dataSourceId);
        if (ds is null)
        {
            return NotFound();
        }

        var allFiles = await fileStorage.ListFilesAsync(ds);
        var toDelete = allFiles.Where(f =>
            f.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var file in toDelete)
        {
            await fileStorage.DeleteFileAsync(ds, file.Path);
        }

        return Ok(new { Deleted = toDelete.Count });
    }

    [HttpPost("move-folder")]
    public async Task<IActionResult> MoveFolder(
        [FromQuery] Guid dataSourceId,
        [FromQuery] string sourcePath,
        [FromQuery] string destinationPath)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);

        if (string.IsNullOrEmpty(sourcePath))
        {
            return BadRequest("Cannot move root folder.");
        }

        if (destinationPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Cannot move a folder into itself.");
        }

        var ds = await GetDataSource(dataSourceId);
        if (ds is null)
        {
            return NotFound();
        }

        var allFiles = await fileStorage.ListFilesAsync(ds);
        var moved = 0;

        foreach (var f in allFiles)
        {
            if (!f.Path.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var newPath = destinationPath + f.Path[sourcePath.Length..];
            await fileStorage.RenameFileAsync(ds, f.Path, newPath);
            moved++;
        }

        return Ok(new { Moved = moved });
    }

    private async Task<DataSource?> GetDataSource(Guid id)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == id && d.UserId == CurrentUserId],
            project: d => d,
            maxResults: 1)).ToList();
        return dataSources.FirstOrDefault();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        path = path.Replace('\\', '/');

        // Resolve ".." and "." segments to prevent path traversal
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == "..")
            {
                if (stack.Count > 0) stack.Pop();
            }
            else if (seg != ".")
            {
                stack.Push(seg);
            }
        }

        var resolved = string.Join("/", stack.Reverse());
        return resolved.Length > 0 ? resolved + "/" : string.Empty;
    }
}
