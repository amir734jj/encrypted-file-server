using System.Text;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.ViewModels;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Models;

namespace Api.Controllers;

[Route("browse")]
public sealed class BrowseController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    ITemplateService templateService,
    UserManager<User> userManager) : ControllerBase
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<AccessTicket> TicketDal => repository.For<AccessTicket>();
    private static readonly FileExtensionContentTypeProvider MimeMap = new();

    [HttpGet]
    public async Task<IActionResult> ListUsers()
    {
        var userId = ResolveUserId();

        if (userId is null)
        {
            userId = await TryBasicAuthUserId();
        }

        if (userId is null)
        {
            Response.Headers["WWW-Authenticate"] = "Basic realm=\"File Server\"";
            return Unauthorized();
        }

        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.UserId == userId],
            project: d => d)).Where(d => d.HasFrontend(FrontendType.Http)).OrderBy(d => d.Name).ToList();

        var html = await templateService.RenderDirectoryListingAsync(new DirectoryListingViewModel
        {
            Title = "/ - File Server",
            CurrentPath = "/browse",
            Entries = dataSources.Select(ds => new EntryViewModel
            {
                Name = ds.Name,
                Href = $"/browse/{ds.Id}/",
                Size = null,
                Modified = ds.CreatedAt
            }).ToList()
        });
        return Content(html, "text/html");
    }

    [HttpGet("{dataSourceId:guid}/{**path}")]
    public async Task<IActionResult> ListOrDownload(Guid dataSourceId, string? path = null, [FromQuery] bool raw = false)
    {
        var (ds, error) = await AuthorizeDataSource(dataSourceId);
        if (error is not null)
        {
            return error;
        }

        path = NormalizePath(path);

        // Check if this is a file download (path points to an actual file)
        var trimmedPath = path.TrimEnd('/');
        if (!string.IsNullOrEmpty(trimmedPath))
        {
            // Check if the path is a file (not a directory prefix)
            var allFiles = await fileStorage.ListFilesAsync(ds!);
            var matchingFile = allFiles.FirstOrDefault(f =>
                string.Equals(f.Path, trimmedPath, StringComparison.OrdinalIgnoreCase));

            if (matchingFile is not null)
            {
                if (raw)
                {
                    var rawStream = await fileStorage.OpenRawStreamAsync(ds!, trimmedPath);
                    var rawFileName = System.IO.Path.GetFileName(trimmedPath);
                    return File(rawStream, "application/octet-stream", rawFileName);
                }

                var fileName = System.IO.Path.GetFileName(trimmedPath);
                MimeMap.TryGetContentType(fileName, out var contentType);
                contentType ??= "application/octet-stream";

                var stream = await fileStorage.OpenDecryptedStreamAsync(ds!, trimmedPath);

                // For streamable media, buffer to temp file for range request support
                if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                    contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    var tempPath = Path.GetTempFileName();
                    var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite,
                        FileShare.None, 81920, FileOptions.DeleteOnClose);
                    await using (stream)
                    {
                        await stream.CopyToAsync(tempStream);
                    }
                    tempStream.Position = 0;
                    Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
                    return File(tempStream, contentType, enableRangeProcessing: true);
                }

                Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
                return File(stream, contentType);
            }
        }

        // Directory listing
        var backendFiles = await fileStorage.ListFilesAsync(ds!);
        var entries = new List<EntryViewModel>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in backendFiles)
        {
            if (!string.IsNullOrEmpty(path) &&
                !f.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = string.IsNullOrEmpty(path) ? f.Path : f.Path[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                var isEncrypted = ds!.Backend.EncryptionMethod != EncryptionMethod.None;
                var href = $"/browse/{dataSourceId}/{(string.IsNullOrEmpty(path) ? "" : path)}{relativePath}";
                entries.Add(new EntryViewModel
                {
                    Name = relativePath,
                    Href = href,
                    RawHref = isEncrypted ? $"{href}?raw=true" : null,
                    Size = f.StoredSize,
                    Modified = f.Modified ?? DateTimeOffset.UtcNow
                });
            }
            else
            {
                var folderName = relativePath[..slashIndex];
                if (seenFolders.Add(folderName))
                {
                    entries.Add(new EntryViewModel
                    {
                        Name = folderName,
                        Href = $"/browse/{dataSourceId}/{(string.IsNullOrEmpty(path) ? "" : path)}{folderName}/"
                    });
                }
            }
        }

        entries = entries
            .OrderBy(e => e.Href.EndsWith('/') ? 0 : 1)
            .ThenBy(e => e.Name)
            .ToList();

        var displayPath = string.IsNullOrEmpty(path) ? $"/{ds!.Name}/" : $"/{ds!.Name}/{path}";
        var parentHref = string.IsNullOrEmpty(path)
            ? "/browse/"
            : $"/browse/{dataSourceId}/{GetParentPath(path)}";

        var html = await templateService.RenderDirectoryListingAsync(new DirectoryListingViewModel
        {
            Title = $"{displayPath} - File Server",
            CurrentPath = $"/browse/{dataSourceId}/{path}",
            Entries = entries,
            ParentHref = parentHref
        });
        return Content(html, "text/html");
    }

    private Guid? ResolveUserId()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return User.GetUserId();
        }

        return null;
    }

    private async Task<Guid?> TryBasicAuthUserId()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var encoded = authHeader["Basic ".Length..].Trim();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var colonIdx = decoded.IndexOf(':');
        var username = colonIdx >= 0 ? decoded[..colonIdx] : decoded;
        var password = colonIdx >= 0 ? decoded[(colonIdx + 1)..] : string.Empty;

        var tickets = (await TicketDal.GetAll(
            filterExprs: [t => t.Username == username
                && t.Password == password
                && t.ExpiresAt > DateTimeOffset.UtcNow],
            project: t => t,
            maxResults: 1)).ToList();

        if (tickets.Count == 0)
        {
            return null;
        }

        var ticket = tickets.First();
        var ticketOwner = await userManager.FindByIdAsync(ticket.UserId.ToString());
        if (ticketOwner is null || !ticketOwner.IsActive)
        {
            return null;
        }

        return ticket.UserId;
    }

    private async Task<(DataSource? ds, IActionResult? error)> AuthorizeDataSource(Guid dataSourceId)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).ToList();

        if (dataSources.Count == 0)
        {
            return (null, NotFound());
        }

        var ds = dataSources.First();
        var httpFrontend = ds.GetFrontend(FrontendType.Http);

        if (httpFrontend is null)
        {
            return (null, NotFound());
        }

        if (httpFrontend.AllowAnonymous)
        {
            return (ds, null);
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUserId = User.GetUserId();
            if (currentUserId != ds.UserId)
            {
                return (null, Forbid());
            }

            return (ds, null);
        }

        // HTTP Basic auth
        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colonIdx = decoded.IndexOf(':');
            var username = colonIdx >= 0 ? decoded[..colonIdx] : decoded;
            var password = colonIdx >= 0 ? decoded[(colonIdx + 1)..] : string.Empty;

            var tickets = (await TicketDal.GetAll(
                filterExprs: [t => t.Username == username
                    && t.Password == password
                    && t.ExpiresAt > DateTimeOffset.UtcNow
                    && t.UserId == ds.UserId],
                project: t => t,
                maxResults: 1)).ToList();

            if (tickets.Count > 0)
            {
                var ticketOwner = await userManager.FindByIdAsync(tickets.First().UserId.ToString());
                if (ticketOwner is not null && ticketOwner.IsActive)
                {
                    return (ds, null);
                }
            }
        }

        Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{ds.Name}\"";
        return (null, Unauthorized());
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == "..") { if (stack.Count > 0) stack.Pop(); }
            else if (seg != ".") stack.Push(seg);
        }

        var resolved = string.Join("/", stack.Reverse());
        return resolved.Length > 0 ? resolved + "/" : string.Empty;
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? "" : trimmed[..(lastSlash + 1)];
    }
}

