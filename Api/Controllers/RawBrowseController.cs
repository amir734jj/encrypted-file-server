using System.Text;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.ViewModels;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace Api.Controllers;

[ApiController]
[Route("raw")]
public sealed class RawBrowseController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    ITemplateService templateService,
    UserManager<User> userManager) : ControllerBase
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<AccessTicket> TicketDal => repository.For<AccessTicket>();

    [HttpGet]
    public async Task<IActionResult> ListDataSources()
    {
        var userId = await ResolveUserId();
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
            Title = "/ - Raw Storage",
            CurrentPath = "/raw",
            AccentColor = "#ff6b6b",
            AccentColorLight = "#cc3333",
            Entries = dataSources.Select(ds => new EntryViewModel
            {
                Name = ds.Name,
                Href = $"/raw/{ds.Id}/",
                Size = null,
                Modified = ds.CreatedAt
            }).ToList()
        });
        return Content(html, "text/html");
    }

    [HttpGet("{dataSourceId:guid}/{**path}")]
    public async Task<IActionResult> ListOrDownload(Guid dataSourceId, string? path = null)
    {
        var (ds, error) = await AuthorizeDataSource(dataSourceId);
        if (error is not null)
        {
            return error;
        }

        path = NormalizePath(path);
        var trimmedPath = path.TrimEnd('/');

        // If path points to a file, serve raw bytes
        if (!string.IsNullOrEmpty(trimmedPath))
        {
            if (await fileStorage.ExistsAsync(ds!, trimmedPath))
            {
                var rawStream = await fileStorage.OpenRawStreamAsync(ds!, trimmedPath);
                var rawFileName = System.IO.Path.GetFileName(trimmedPath);
                return File(rawStream, "application/octet-stream", rawFileName);
            }
        }

        // Directory listing — use raw (non-decrypted) storage names
        var backendFiles = await fileStorage.ListFilesRawAsync(ds!);
        var entries = new List<EntryViewModel>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in backendFiles)
        {
            var filePath = f.Path;

            // Skip directory markers and files outside current path
            if (filePath.EndsWith('/'))
            {
                filePath = filePath[..^1];
                if (!string.IsNullOrEmpty(path) &&
                    !filePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relDir = string.IsNullOrEmpty(path) ? filePath : filePath[path.Length..];
                var dirSlash = relDir.IndexOf('/');
                var dirName = dirSlash < 0 ? relDir : relDir[..dirSlash];
                if (!string.IsNullOrEmpty(dirName))
                    seenFolders.Add(dirName);
                continue;
            }

            if (!string.IsNullOrEmpty(path) &&
                !filePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = string.IsNullOrEmpty(path) ? filePath : filePath[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                entries.Add(new EntryViewModel
                {
                    Name = relativePath,
                    Href = $"/raw/{dataSourceId}/{(string.IsNullOrEmpty(path) ? "" : path)}{relativePath}",
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
                        Href = $"/raw/{dataSourceId}/{(string.IsNullOrEmpty(path) ? "" : path)}{folderName}/"
                    });
                }
            }
        }

        // Add folder entries for empty dirs that were only seen as markers
        foreach (var folder in seenFolders)
        {
            if (!entries.Any(e => e.Name.Equals(folder, StringComparison.OrdinalIgnoreCase) && e.Href.EndsWith('/')))
            {
                entries.Add(new EntryViewModel
                {
                    Name = folder,
                    Href = $"/raw/{dataSourceId}/{(string.IsNullOrEmpty(path) ? "" : path)}{folder}/"
                });
            }
        }

        entries = entries
            .OrderBy(e => e.Href.EndsWith('/') ? 0 : 1)
            .ThenBy(e => e.Name)
            .ToList();

        var displayPath = string.IsNullOrEmpty(path) ? $"/{ds!.Name}/ (raw)" : $"/{ds!.Name}/{path} (raw)";
        var parentHref = string.IsNullOrEmpty(path)
            ? "/raw/"
            : $"/raw/{dataSourceId}/{GetParentPath(path)}";

        var html = await templateService.RenderDirectoryListingAsync(new DirectoryListingViewModel
        {
            Title = $"{displayPath} - Raw Storage",
            CurrentPath = $"/raw/{dataSourceId}/{path}",
            Entries = entries,
            ParentHref = parentHref,
            Badge = "RAW",
            Subtitle = "Showing raw storage files. Downloads are served as-is without decryption.",
            NameHeader = "Storage Name",
            DateHeader = "Created",
            AccentColor = "#ff6b6b",
            AccentColorLight = "#cc3333"
        });
        return Content(html, "text/html");
    }

    private async Task<Guid?> ResolveUserId()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return User.GetUserId();
        }

        return await TryBasicAuthUserId();
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
        var owner = await userManager.FindByIdAsync(ticket.UserId.ToString());
        if (owner is null || !owner.IsActive)
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
                var owner = await userManager.FindByIdAsync(tickets.First().UserId.ToString());
                if (owner is not null && owner.IsActive)
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
